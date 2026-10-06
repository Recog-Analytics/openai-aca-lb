using System.Net;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using openai_loadbalancer.Dashboard;
using openai_loadbalancer.Discovery;
using openai_loadbalancer.Health;
using openai_loadbalancer.Operations;

namespace openai_loadbalancer.Tests;

public class DashboardPublisherTests
{
    [Fact]
    public async Task EmptyIngestUrlDisablesPublisherAndQueue()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDashboardPublisher(builder.Configuration);
        using var host = builder.Build();
        await host.StartAsync();
        Assert.Null(host.Services.GetService<DashboardEventBuffer>());
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), service => service is DashboardPublisher);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("Production", "Fake", "https://dashboard/ingest", "api://dashboard")]
    [InlineData("Production", "ManagedIdentity", "http://dashboard/ingest", "api://dashboard")]
    [InlineData("Production", "ManagedIdentity", "https://dashboard/ingest", "")]
    [InlineData("Development", "Unknown", "http://dashboard/ingest", "")]
    public async Task PublisherRejectsUnsafeOrIncompleteConfiguration(string environment, string credential, string url, string audience)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dashboard:Credential"] = credential, ["Dashboard:IngestUrl"] = url, ["Dashboard:Audience"] = audience
        });
        builder.Services.AddDiscovery(builder.Configuration);
        builder.Services.AddDashboardPublisher(builder.Configuration);
        using var host = builder.Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public void OverflowDropsOldestCountsDropsAndPreservesAllAttemptCounts()
    {
        using var buffer = new DashboardEventBuffer();
        for (var index = 0; index < 10_500; index++)
            buffer.Add(DashboardStoreTests.Request(index.ToString()));
        Assert.Equal(500, buffer.Dropped);
        var batch = buffer.Drain();
        Assert.Equal(200, batch.Requests.Count);
        Assert.All(batch.Requests, item => Assert.True(int.Parse(item.Id) >= 500));
        Assert.Equal(200, batch.Requests.Select(item => item.Id).Distinct().Count());
        Assert.Equal(10_500, Assert.Single(batch.Counts).Count);
        Assert.Equal(10_500, Assert.Single(batch.Routes).Count);
        Assert.Empty(buffer.Drain().Requests);
        Assert.Empty(buffer.Drain().Counts);
        Assert.Empty(buffer.Drain().Routes);
    }

    [Fact]
    public void RoutesCountEveryRequestByCallerModelZoneStatusAndAttemptChain()
    {
        using var buffer = new DashboardEventBuffer();
        var direct = DashboardStoreTests.Request("direct");
        var fallback = direct with
        {
            Attempts =
            [
                new("chat", "france", "francecentral", 1, 429, 30, "Throttled", "throttled", "france"),
                new("chat", "account", "swedencentral", 1, 200, 20, "Success", null, "deployment")
            ]
        };
        var refused = direct with { Status = 503, Attempts = [], Outcome = "error" };
        for (var index = 0; index < 3; index++)
            buffer.Add(direct with { Id = $"d{index}" });
        buffer.Add(direct with { Id = "other-caller", Caller = "batch" });
        buffer.Add(fallback with { Id = "f1" });
        buffer.Add(fallback with { Id = "f2" });
        buffer.Add(refused with { Id = "r1" });
        var routes = buffer.Drain().Routes;
        Assert.Equal(4, routes.Count);
        Assert.Equal(3, routes.Single(item => item.Caller == "dev" && item.Hops.Count == 1).Count);
        Assert.Equal(1, routes.Single(item => item.Caller == "batch").Count);
        var chain = routes.Single(item => item.Hops.Count == 2);
        Assert.Equal(2, chain.Count);
        Assert.Equal([new("france", "Throttled"), new("deployment", "Success")], chain.Hops);
        var lb = routes.Single(item => item.Hops.Count == 0);
        Assert.Equal((503, "gpt-4o@1", "eu", 1L), (lb.Status, lb.ModelKey, lb.Zone, lb.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedOrFailedDeliveryNeverBlocksRequestHistoryAndNeverRetries(bool fail)
    {
        var handler = new ControlledHandler();
        using var client = new HttpClient(handler);
        using var buffer = new DashboardEventBuffer();
        var history = new RequestHistory(buffer);
        history.Add(DashboardStoreTests.Request("initial"));
        var credential = new RecordingCredential();
        using var publisher = Publisher(buffer, client, credential);
        var push = publisher.PublishAsync(CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 1000; index++)
            history.Add(DashboardStoreTests.Request(index.ToString()));
        Assert.False(push.IsCompleted);
        Assert.Equal(500, history.GetRecent(500).Count);
        handler.Complete.TrySetResult(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted);
        await push.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.Calls);
        Assert.Equal("Bearer dashboard-token", handler.Authorization);
        Assert.Equal("api://dashboard/.default", Assert.Single(credential.Scopes));
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("initial", Assert.Single(json.RootElement.GetProperty("requests").EnumerateArray()).GetProperty("id").GetString());
        Assert.DoesNotContain("dashboard-token", handler.Body);
        var next = buffer.Drain();
        Assert.Equal(200, next.Requests.Count);
        Assert.Equal(1000, Assert.Single(next.Counts).Count);
        Assert.Equal(1000, Assert.Single(next.Routes).Count);
        Assert.Equal(1, json.RootElement.GetProperty("routes").EnumerateArray().Single().GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task TimeoutIncludesTokenAcquisitionAndLeavesRequestPathAvailable()
    {
        var clock = new TestClock();
        using var buffer = new DashboardEventBuffer();
        var history = new RequestHistory(buffer);
        var credential = new BlockedCredential();
        using var client = new HttpClient();
        using var publisher = Publisher(buffer, client, credential, clock);
        var push = publisher.PublishAsync(CancellationToken.None);
        await credential.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        history.Add(DashboardStoreTests.Request("while-blocked"));
        clock.Advance(TimeSpan.FromSeconds(5));
        await push.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(history.GetRecent(500));
        Assert.Single(buffer.Drain().Requests);
    }

    [Fact]
    public async Task HttpTimeoutCancelsThePushWithoutRetry()
    {
        var clock = new TestClock();
        var handler = new ControlledHandler();
        using var client = new HttpClient(handler);
        using var buffer = new DashboardEventBuffer();
        using var publisher = Publisher(buffer, client, new RecordingCredential(), clock);
        var push = publisher.PublishAsync(CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(5));
        await push.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SlowPushDoesNotStartOverlappingBatchesAndShutdownCancelsIt()
    {
        var clock = new TestClock();
        var handler = new ControlledHandler();
        using var client = new HttpClient(handler);
        using var buffer = new DashboardEventBuffer();
        using var publisher = Publisher(buffer, client, new RecordingCredential(), clock);
        await publisher.StartAsync(CancellationToken.None);
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        buffer.Add(DashboardStoreTests.Request("queued"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, handler.Calls);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await publisher.StopAsync(deadline.Token);
        Assert.Single(buffer.Drain().Requests);
        Assert.Equal(1, handler.Calls);
    }

    private static DashboardPublisher Publisher(DashboardEventBuffer buffer, HttpClient client, TokenCredential credential, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var health = new HealthState(clock);
        return new(buffer, new DiscoveryState(health), health, new ClientFactory(client), credential,
            Options.Create(new DashboardPublisherOptions { IngestUrl = "http://localhost/ingest", Audience = "api://dashboard" }),
            clock, new TestLogger<DashboardPublisher>());
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingCredential : TokenCredential
    {
        public string[] Scopes { get; private set; } = [];
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = requestContext.Scopes;
            return ValueTask.FromResult(new AccessToken("dashboard-token", DateTimeOffset.MaxValue));
        }
    }

    private sealed class BlockedCredential : TokenCredential
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class ControlledHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpStatusCode> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Started.TrySetResult();
            return new(await Complete.Task.WaitAsync(cancellationToken));
        }
    }
}
