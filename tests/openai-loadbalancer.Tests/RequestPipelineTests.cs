using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using openai_loadbalancer.Configuration;
using openai_loadbalancer.Discovery;
using openai_loadbalancer.Health;
using openai_loadbalancer.Pipeline;
using openai_loadbalancer.Operations;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Tests;

public class RequestPipelineTests
{
    private const string Model = "gpt-4o@2024-11-20";
    private const string Key = "lbk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Path = "/openai/deployments/" + Model + "/chat/completions?api-version=2024-10-21";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptsCallerKeysAndOnlyForwardsManagedIdentity(bool bearer)
    {
        var received = new TaskCompletionSource<ReceivedRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backend = await Server.StartAsync(async context =>
        {
            received.TrySetResult(await ReceivedRequest.ReadAsync(context));
            await context.Response.WriteAsync("ok");
        });
        await using var proxy = await Proxy.StartAsync([Source(backend, "actual-deployment")]);
        using var request = Request(Path, bearer: bearer);
        request.Headers.Add("x-lb-data-zone", "eu");
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Bearer managed-identity-token", captured.Authorization);
        Assert.Null(captured.ApiKey);
        Assert.Equal("/openai/deployments/actual-deployment/chat/completions", captured.Path);
        Assert.Equal("?api-version=2024-10-21", captured.Query);
        Assert.Equal("actual-deployment", Header(response, "x-lb-deployment"));
        Assert.Equal("westeurope", Header(response, "x-lb-region"));
        Assert.Equal("1", Header(response, "x-lb-attempts"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("lbk_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    [InlineData("invalid")]
    public async Task RejectsMissingOrUnknownKeysBeforeForwarding(string? key)
    {
        await using var backend = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([Source(backend)]);
        using var request = Request(Path, key: key);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, backend.RequestCount);
        using var health = await proxy.Client.GetAsync("/healthz");
        using var ready = await proxy.Client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task MetricsDescribeRetryAndHeaderLatencyWithoutCountingTwoRequests()
    {
        var measurements = new ConcurrentQueue<(string Name, double Value, string Caller)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == OperationsTelemetry.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        void Capture(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dimensions = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
            if (dimensions.GetValueOrDefault("deployment") is string id && id.StartsWith("/accounts/metric-", StringComparison.Ordinal))
                measurements.Enqueue((instrument.Name, value, (string)dimensions["caller"]!));
        }
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Capture(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Capture(instrument, value, tags));
        listener.Start();
        await using var primary = await Server.StartAsync(context =>
        {
            context.Response.StatusCode = 429;
            context.Response.Headers.RetryAfter = "10";
            return context.Response.WriteAsync("throttled");
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("ok"));
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "metric-primary", sku: "ProvisionedManaged"), Source(fallback, "metric-fallback")]);
        using var request = Request(Path);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WaitUntilAsync(() => measurements.Any(item => item.Name == "lb.requests"));
        Assert.Single(measurements, item => item.Name == "lb.requests");
        Assert.Single(measurements, item => item.Name == "lb.retries");
        Assert.Equal(2, measurements.Count(item => item.Name == "lb.ttfb"));
        Assert.Equal(10, Assert.Single(measurements, item => item.Name == "lb.throttle_time").Value);
        Assert.All(measurements, item => Assert.Equal("integration-caller", item.Caller));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    public async Task UnauthenticatedRequestsCannotIncreaseRetryBudget(string? key)
    {
        var clock = new TestClock();
        var budget = new RetryBudget(clock);
        for (var retry = 0; retry < 100; retry++)
            Assert.True(budget.TryAcquireRetry());
        await using var backend = await Server.StartAsync(context => context.Response.WriteAsync("ok"));
        await using var proxy = await Proxy.StartAsync([Source(backend)], retryBudget: budget);
        // Crossing 500 originals would raise the 20% allowance above its exhausted floor.
        for (var count = 0; count < 510; count++)
        {
            using var request = Request(Path, key: key);
            using var response = await proxy.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.False(budget.TryAcquireRetry());
        Assert.Equal(0, backend.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminStateUsesCallerAuthenticationAndOmitsSecrets(bool bearer)
    {
        await using var backend = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([Source(backend)]);
        using var anonymous = await proxy.Client.GetAsync("/admin/state");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var request = Request("/admin/state", bearer: bearer);
        request.Method = HttpMethod.Get;
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Key, text);
        Assert.DoesNotContain("sha256:", text);
        Assert.DoesNotContain("callers", text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(Model, json.RootElement.GetProperty("deployments")[0].GetProperty("model").GetString());
        Assert.Equal("Healthy", json.RootElement.GetProperty("deployments")[0].GetProperty("health").GetProperty("state").GetString());
        Assert.Equal(0, backend.RequestCount);
    }

    [Fact]
    public async Task AdminStateReportsUnavailableBeforeDiscovery()
    {
        await using var backend = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([Source(backend)], publishSnapshot: false);
        using var request = Request("/admin/state");
        request.Method = HttpMethod.Get;
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, backend.RequestCount);
    }

    [Fact]
    public async Task DefaultZoneEnforcesResidencyAndGlobalAllowsAllZones()
    {
        await using var eu = await Server.StartAsync(context => context.Response.WriteAsync("eu"));
        await using var us = await Server.StartAsync(context => context.Response.WriteAsync("us"));
        await using var proxy = await Proxy.StartAsync([
            Source(us, "us-primary", sku: "ProvisionedManaged", region: "eastus"),
            Source(eu, "eu-secondary")]);
        using var defaultRequest = Request(Path);
        using var defaultResponse = await proxy.Client.SendAsync(defaultRequest);
        Assert.Equal("eu", await defaultResponse.Content.ReadAsStringAsync());
        using var forbiddenRequest = Request(Path);
        forbiddenRequest.Headers.Add("x-lb-data-zone", "us");
        using var forbidden = await proxy.Client.SendAsync(forbiddenRequest);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var globalRequest = Request(Path);
        globalRequest.Headers.Add("x-lb-data-zone", "global");
        using var globalResponse = await proxy.Client.SendAsync(globalRequest);
        Assert.Equal("us", await globalResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, eu.RequestCount);
        Assert.Equal(1, us.RequestCount);
    }

    [Fact]
    public async Task RewritesOnlyDeploymentPathSegmentAndV1ModelField()
    {
        var captured = new ConcurrentQueue<ReceivedRequest>();
        await using var backend = await Server.StartAsync(async context =>
        {
            captured.Enqueue(await ReceivedRequest.ReadAsync(context));
            await context.Response.WriteAsync("ok");
        });
        await using var proxy = await Proxy.StartAsync([Source(backend, "actual-deployment")]);
        using var pathRequest = Request($"/openai/deployments/{Model}/extensions/{Model}?echo={Model}");
        using var pathResponse = await proxy.Client.SendAsync(pathRequest);
        Assert.Equal(HttpStatusCode.OK, pathResponse.StatusCode);
        using var v1Request = Request("/openai/v1/chat/completions", "{\"model\":\"gpt-4o@2024-11-20\",\"messages\":[{\"content\":\"gpt-4o@2024-11-20\"}],\"stream\":false}");
        using var v1Response = await proxy.Client.SendAsync(v1Request);
        Assert.Equal(HttpStatusCode.OK, v1Response.StatusCode);
        Assert.True(captured.TryDequeue(out var path));
        Assert.Equal($"/openai/deployments/actual-deployment/extensions/{Model}", path.Path);
        Assert.Equal($"?echo={Model}", path.Query);
        Assert.True(captured.TryDequeue(out var v1));
        Assert.Equal("/openai/v1/chat/completions", v1.Path);
        using var body = JsonDocument.Parse(v1.Body);
        Assert.Equal("actual-deployment", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(Model, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task DoubleEncodedSuffixCannotEscapeSelectedDeploymentOrResidency()
    {
        const string expectedPath = "/openai/deployments/selected-deployment/%2e%2e/global-deployment/chat/completions";
        var captured = new TaskCompletionSource<ReceivedRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forbiddenCalls = 0;
        await using var backend = await Server.StartAsync(async context =>
        {
            var request = await ReceivedRequest.ReadAsync(context);
            captured.TrySetResult(request);
            if (!request.Path.StartsWith("/openai/deployments/selected-deployment/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref forbiddenCalls);
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("unintended deployment");
                return;
            }
            await context.Response.WriteAsync("selected deployment");
        });
        await using var proxy = await Proxy.StartAsync([
            Source(backend, "selected-deployment"),
            Source(backend, "global-deployment", sku: "GlobalProvisionedManaged", region: "eastus")]);
        using var request = Request($"/openai/deployments/{Model}/%252e%252e/global-deployment/chat/completions");
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("selected deployment", await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedPath, (await captured.Task.WaitAsync(TimeSpan.FromSeconds(5))).Path);
        Assert.Equal("selected-deployment", Header(response, "x-lb-deployment"));
        Assert.Equal("westeurope", Header(response, "x-lb-region"));
        Assert.Equal(0, Volatile.Read(ref forbiddenCalls));
        Assert.Equal(1, backend.RequestCount);
    }

    [Fact]
    public async Task OpenAICompatibleV1PathUsesAzurePrefixAndPreservesQueryAndBody()
    {
        var captured = new TaskCompletionSource<ReceivedRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backend = await Server.StartAsync(async context =>
        {
            var request = await ReceivedRequest.ReadAsync(context);
            captured.TrySetResult(request);
            if (request.Path != "/openai/v1/chat/completions")
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync("Azure v1 prefix required");
                return;
            }
            await context.Response.WriteAsync("ok");
        });
        await using var proxy = await Proxy.StartAsync([Source(backend, "actual-deployment")]);
        using var request = Request("/v1/chat/completions?api-version=preview",
            "{\"model\":\"gpt-4o@2024-11-20\",\"messages\":[{\"content\":\"keep this message\"}],\"stream\":false}");
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        var received = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("/openai/v1/chat/completions", received.Path);
        Assert.Equal("?api-version=preview", received.Query);
        using var body = JsonDocument.Parse(received.Body);
        Assert.Equal("actual-deployment", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("keep this message", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task StreamsEachChunkBeforeBackendCompletesAndWithoutTotalDurationTimeout()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backend = await Server.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: first\n\n");
            await context.Response.Body.FlushAsync();
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("data: second\n\n");
            await context.Response.Body.FlushAsync();
        });
        await using var proxy = await Proxy.StartAsync([Source(backend)], overallTimeout: TimeSpan.FromMilliseconds(250));
        try
        {
            using var request = Request(Path, "{\"stream\":true}");
            using var response = await proxy.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                .WaitAsync(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            Assert.Equal("data: first", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("", await reader.ReadLineAsync());
            await Task.Delay(350);
            release.TrySetResult();
            Assert.Equal("data: second", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("1", Header(response, "x-lb-attempts"));
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(1, backend.RequestCount);
    }

    [Fact]
    public async Task StreamingFailureAfterFirstChunkNeverRetriesAndRecordsFailure()
    {
        var abort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broken = await Server.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: first\n\n");
            await context.Response.Body.FlushAsync();
            await abort.Task.WaitAsync(context.RequestAborted);
            context.Abort();
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(broken, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        try
        {
            using var request = Request(Path, "{\"stream\":true}");
            using var response = await proxy.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            Assert.Equal("data: first", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("", await reader.ReadLineAsync());
            abort.TrySetResult();
            await Assert.ThrowsAnyAsync<IOException>(async () => await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await WaitUntilAsync(() => proxy.Health.Outcomes.Contains(HealthOutcome.Failure));
            Assert.Equal(0, fallback.RequestCount);
            Assert.Equal(1, broken.RequestCount);
        }
        finally
        {
            abort.TrySetResult();
        }
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task RetriesRetriableStatusAndReplaysExactBody(int status)
    {
        var bodies = new ConcurrentQueue<string>();
        await using var primary = await Server.StartAsync(async context =>
        {
            bodies.Enqueue((await ReceivedRequest.ReadAsync(context)).Body);
            context.Response.StatusCode = status;
            context.Response.Headers.RetryAfter = "120";
            await context.Response.WriteAsync("primary error must not leak");
        });
        await using var fallback = await Server.StartAsync(async context =>
        {
            bodies.Enqueue((await ReceivedRequest.ReadAsync(context)).Body);
            await context.Response.WriteAsync("fallback success");
        });
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        const string body = "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}";
        using var request = Request(Path, body);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fallback success", await response.Content.ReadAsStringAsync());
        Assert.Equal("2", Header(response, "x-lb-attempts"));
        Assert.Equal("fallback", Header(response, "x-lb-deployment"));
        Assert.Equal(new[] { body, body }, bodies.ToArray());
        Assert.Contains(status == 429 ? HealthOutcome.Throttled : HealthOutcome.Failure, proxy.Health.Outcomes);
        Assert.Equal(1, primary.RequestCount);
        Assert.Equal(1, fallback.RequestCount);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(413)]
    public async Task OtherClientErrorsPassThroughWithoutRetryOrHealthFailure(int status)
    {
        await using var primary = await Server.StartAsync(async context =>
        {
            context.Response.StatusCode = status;
            await context.Response.WriteAsync("client error");
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        using var request = Request(Path);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("client error", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, fallback.RequestCount);
        Assert.DoesNotContain(HealthOutcome.Failure, proxy.Health.Outcomes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreHeaderTimeoutRetriesAndCancelsSlowBackend(bool streaming)
    {
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var slow = await Server.StartAsync(async context =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) { aborted.TrySetResult(); }
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("fallback"));
        await using var proxy = await Proxy.StartAsync([
            Source(slow, "slow", sku: "ProvisionedManaged"), Source(fallback, "fallback")],
            ttfbTimeout: TimeSpan.FromMilliseconds(150));
        using var request = Request(Path, streaming ? "{\"stream\":true}" : "{}");
        using var response = await proxy.Client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fallback", await response.Content.ReadAsStringAsync());
        Assert.Equal("2", Header(response, "x-lb-attempts"));
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(HealthOutcome.Failure, proxy.Health.Outcomes);
    }

    [Fact]
    public async Task ClientAbortCancelsBackendWithoutRetryOrHealthFailure()
    {
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backend = await Server.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: first\n\n");
            await context.Response.Body.FlushAsync();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) { aborted.TrySetResult(); }
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(backend, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        using var cancellation = new CancellationTokenSource();
        using var request = Request(Path, "{\"stream\":true}");
        using var response = await proxy.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        using var stream = await response.Content.ReadAsStreamAsync();
        var bytes = new byte[100];
        Assert.True(await stream.ReadAsync(bytes, cancellation.Token) > 0);
        cancellation.Cancel();
        response.Dispose();
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !proxy.Health.Outcomes.IsEmpty);
        Assert.DoesNotContain(HealthOutcome.Failure, proxy.Health.Outcomes);
        Assert.DoesNotContain(HealthOutcome.AccountFailure, proxy.Health.Outcomes);
        Assert.Equal(0, fallback.RequestCount);
    }

    [Fact]
    public async Task AttemptsStopAtThreeAndNeverReuseDeployment()
    {
        var servers = new List<Server>();
        try
        {
            for (var index = 0; index < 4; index++)
                servers.Add(await Server.StartAsync(context =>
                {
                    context.Response.StatusCode = 500;
                    return context.Response.WriteAsync("failure");
                }));
            await using var proxy = await Proxy.StartAsync(servers.Select((server, index) => Source(server, "deployment-" + index)).ToArray());
            using var request = Request(Path);
            using var response = await proxy.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("3", Header(response, "x-lb-attempts"));
            Assert.Equal(3, servers.Sum(server => server.RequestCount));
            Assert.All(servers, server => Assert.InRange(server.RequestCount, 0, 1));
        }
        finally
        {
            foreach (var server in servers)
                await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExhaustedRetryBudgetReturnsFirstBackendError()
    {
        await using var primary = await Server.StartAsync(context =>
        {
            context.Response.StatusCode = 500;
            return context.Response.WriteAsync("first error");
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")], retryBudget: new NoRetries());
        using var request = Request(Path);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("first error", await response.Content.ReadAsStringAsync());
        Assert.Equal("1", Header(response, "x-lb-attempts"));
        Assert.Equal(0, fallback.RequestCount);
    }

    [Fact]
    public async Task RequestBodyOverSixteenMegabytesReturns413WithoutForwarding()
    {
        await using var backend = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([Source(backend)]);
        using var request = Request(Path, new string('x', 16 * 1024 * 1024 + 1));
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, backend.RequestCount);
    }

    [Fact]
    public async Task ClientDeadlineStopsBeforeTtfbTimeoutAndDoesNotStartFallback()
    {
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var slow = await Server.StartAsync(async context =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) { aborted.TrySetResult(); }
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(slow, "slow", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        using var request = Request(Path);
        request.Headers.Add("x-lb-timeout-ms", "150");
        using var response = await proxy.Client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("1", Header(response, "x-lb-attempts"));
        Assert.Equal(0, fallback.RequestCount);
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task MisconfiguredDeploymentIsDisabledAndRequestFallsBack(int status)
    {
        await using var primary = await Server.StartAsync(context =>
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync("{\"error\":{\"code\":\"DeploymentNotFound\",\"message\":\"deployment missing\"}}");
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("fallback"));
        var source = Source(primary, "primary", sku: "ProvisionedManaged");
        await using var proxy = await Proxy.StartAsync([source, Source(fallback, "fallback")]);
        using var request = Request(Path);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fallback", await response.Content.ReadAsStringAsync());
        Assert.Equal(DeploymentHealth.Disabled,
            proxy.Health.GetSnapshot().Single(item => item.DeploymentId.EndsWith("/primary", StringComparison.Ordinal)).State);
        Assert.Contains(HealthOutcome.Misconfigured, proxy.Health.Outcomes);
    }

    [Fact]
    public async Task WaitsForPreexistingThrottleOnceWhenDeadlineAllows()
    {
        await using var backend = await Server.StartAsync(context => context.Response.WriteAsync("recovered"));
        var source = Source(backend);
        await using var proxy = await Proxy.StartAsync([source]);
        proxy.Health.TryAcquire(source.AccountId + "/deployments/" + source.DeploymentName)!
            .Complete(HealthOutcome.Throttled, retryAfterMilliseconds: "1000");
        using var request = Request(Path);
        request.Headers.Add("x-lb-timeout-ms", "3000");
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("recovered", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, backend.RequestCount);
        Assert.Equal("1", Header(response, "x-lb-attempts"));
    }

    [Fact]
    public async Task ThrottleLongerThanDeadlineReturns429WithRetryAfter()
    {
        await using var backend = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        var source = Source(backend);
        await using var proxy = await Proxy.StartAsync([source]);
        proxy.Health.TryAcquire(source.AccountId + "/deployments/" + source.DeploymentName)!
            .Complete(HealthOutcome.Throttled, retryAfter: "120");
        using var request = Request(Path);
        request.Headers.Add("x-lb-timeout-ms", "100");
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.InRange(response.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 119, 120);
        Assert.Equal(0, backend.RequestCount);
    }

    [Fact]
    public async Task RefusedConnectionOpensWholeAccountAndRetriesDifferentAccount()
    {
        var refused = await Server.StartAsync(context => context.Response.WriteAsync("unused"));
        var primary = Source(refused, "primary", sku: "ProvisionedManaged");
        await refused.DisposeAsync();
        await using var sibling = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("fallback"));
        var siblingSource = Source(sibling, "sibling") with { AccountId = primary.AccountId, AccountName = primary.AccountName };
        await using var proxy = await Proxy.StartAsync([primary, siblingSource, Source(fallback, "fallback")]);
        using var request = Request(Path);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fallback", await response.Content.ReadAsStringAsync());
        Assert.Equal("2", Header(response, "x-lb-attempts"));
        Assert.Equal(0, sibling.RequestCount);
        Assert.Contains(HealthOutcome.AccountFailure, proxy.Health.Outcomes);
        Assert.All(proxy.Health.GetSnapshot().Where(item => item.AccountId == primary.AccountId),
            item => Assert.True(item.AccountOpen));
    }

    [Fact]
    public async Task Ordinary404ResponsePassesThroughExactBodyWithoutRetry()
    {
        const string body = "{\"error\":{\"code\":\"NotFound\",\"message\":\"unknown operation\"}}";
        await using var primary = await Server.StartAsync(context =>
        {
            context.Response.StatusCode = 404;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(body);
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        using var request = Request(Path);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, fallback.RequestCount);
        Assert.DoesNotContain(HealthOutcome.Misconfigured, proxy.Health.Outcomes);
        Assert.DoesNotContain(HealthOutcome.Failure, proxy.Health.Outcomes);
    }

    [Theory]
    [InlineData("NotFound", false)]
    [InlineData("DeploymentNotFound", true)]
    public async Task SlowJson404BodyFinishesAfterDeadlineWithoutRetry(string errorCode, bool misconfigured)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = "{\"error\":{\"code\":\"" + errorCode + "\",\"message\":\"delayed error body\"}}";
        await using var primary = await Server.StartAsync(async context =>
        {
            context.Response.StatusCode = 404;
            context.Response.ContentType = "application/json";
            await context.Response.StartAsync();
            await context.Response.Body.FlushAsync();
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync(body);
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")],
            overallTimeout: TimeSpan.FromMilliseconds(400));
        try
        {
            using var request = Request(Path);
            var send = proxy.Client.SendAsync(request);
            await WaitUntilAsync(() => proxy.Health.TtfbCount > 0);
            await Task.Delay(500);
            release.TrySetResult();
            using var response = await send.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(body, await response.Content.ReadAsStringAsync());
            Assert.Equal("1", Header(response, "x-lb-attempts"));
            Assert.Equal("primary", Header(response, "x-lb-deployment"));
            Assert.Equal(0, fallback.RequestCount);
            await WaitUntilAsync(() => !proxy.Health.Outcomes.IsEmpty);
            Assert.Contains(misconfigured ? HealthOutcome.Misconfigured : HealthOutcome.Ignored, proxy.Health.Outcomes);
            Assert.DoesNotContain(HealthOutcome.Failure, proxy.Health.Outcomes);
            Assert.Equal(misconfigured ? DeploymentHealth.Disabled : DeploymentHealth.Healthy,
                proxy.Health.GetSnapshot().Single(item => item.DeploymentId.EndsWith("/primary", StringComparison.Ordinal)).State);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task BodyFailureImmediatelyAfterResponseHeadersNeverRetries()
    {
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = await Server.StartAsync(async context =>
        {
            context.Response.ContentLength = 100;
            await context.Response.StartAsync();
            await context.Response.Body.FlushAsync();
            headersSent.TrySetResult();
            await abort.Task.WaitAsync(context.RequestAborted);
            context.Abort();
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        try
        {
            using var request = Request(Path);
            var send = proxy.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            await headersSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => proxy.Health.TtfbCount > 0);
            abort.TrySetResult();
            try
            {
                using var response = await send.WaitAsync(TimeSpan.FromSeconds(5));
                await response.Content.ReadAsByteArrayAsync();
                Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
                Assert.Equal("1", Header(response, "x-lb-attempts"));
                Assert.Equal("primary", Header(response, "x-lb-deployment"));
            }
            catch (HttpRequestException)
            {
                // Either a truncated response or a generated 502 exposes the backend body failure.
            }
            await WaitUntilAsync(() => !proxy.Health.Outcomes.IsEmpty);
            Assert.Contains(HealthOutcome.Failure, proxy.Health.Outcomes);
            Assert.Equal(0, fallback.RequestCount);
        }
        finally
        {
            abort.TrySetResult();
        }
    }

    private static HttpRequestMessage Request(string path, string body = "{}", string? key = Key, bool bearer = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (key != null)
        {
            if (bearer)
                request.Headers.Authorization = new("Bearer", key);
            else
                request.Headers.Add("api-key", key);
        }
        return request;
    }

    private static string Header(HttpResponseMessage response, string name) => Assert.Single(response.Headers.GetValues(name));

    private static DiscoveredDeployment Source(Server server, string name = "backend", string sku = "Standard", string region = "westeurope") =>
        new("/accounts/" + name, name, server.Address, region, name, new("gpt-4o", "2024-11-20"), sku, 100, "Succeeded");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed record ReceivedRequest(string Path, string Query, string? Authorization, string? ApiKey, string Body)
    {
        public static async Task<ReceivedRequest> ReadAsync(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body);
            return new(context.Request.Path, context.Request.QueryString.Value ?? "",
                context.Request.Headers.Authorization.FirstOrDefault(), context.Request.Headers["api-key"].FirstOrDefault(),
                await reader.ReadToEndAsync(context.RequestAborted));
        }
    }

    private sealed class Server(WebApplication app) : IAsyncDisposable
    {
        private int requestCount;
        public Uri Address => new(app.Urls.Single());
        public int RequestCount => Volatile.Read(ref requestCount);

        public static async Task<Server> StartAsync(RequestDelegate handler)
        {
            var builder = NewBuilder();
            var app = builder.Build();
            var server = new Server(app);
            app.Run(async context =>
            {
                Interlocked.Increment(ref server.requestCount);
                await handler(context);
            });
            await app.StartAsync();
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class Proxy(WebApplication app, RecordingHealth health) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new() { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
        public RecordingHealth Health => health;

        public static async Task<Proxy> StartAsync(DiscoveredDeployment[] deployments, TimeSpan? ttfbTimeout = null,
            TimeSpan? overallTimeout = null, IRetryBudget? retryBudget = null, bool publishSnapshot = true)
        {
            var builder = NewBuilder();
            var health = new RecordingHealth();
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddSingleton<IHealthState>(health);
            builder.Services.AddSingleton<IRetryBudget>(retryBudget ?? new RetryBudget(TimeProvider.System));
            builder.Services.AddSingleton<DiscoveryState>();
            builder.Services.AddRequestPipeline(builder.Configuration);
            builder.Services.AddSingleton<IBackendTokenProvider>(new FakeTokenProvider());
            builder.Services.Configure<RequestPipelineOptions>(options =>
            {
                if (ttfbTimeout.HasValue)
                    options.NonStreamingTtfbTimeout = options.StreamingTtfbTimeout = ttfbTimeout.Value;
                if (overallTimeout.HasValue)
                    options.OverallTimeout = overallTimeout.Value;
            });
            builder.Services.AddHealthChecks();
            var app = builder.Build();
            var table = new RoutingTableBuilder().Build(deployments,
                new RegionGeography(new Dictionary<string, string> { ["westeurope"] = "Europe", ["eastus"] = "United States" }), new());
            var callers = new CallersConfiguration
            {
                Callers = [new Caller
                {
                    Name = "integration-caller", Zones = ["eu", "global"],
                    KeyHashes = ["sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key))).ToLowerInvariant()]
                }]
            };
            var state = app.Services.GetRequiredService<DiscoveryState>();
            if (publishSnapshot)
                typeof(DiscoveryState).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(state, [new DiscoverySnapshot(table, callers, DateTimeOffset.UtcNow), new RoutingOverrides()]);
            app.MapHealthChecks("/healthz");
            app.MapDiscoveryReadiness();
            app.MapAdminState();
            app.MapRequestPipeline();
            await app.StartAsync();
            return new(app, health);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static WebApplicationBuilder NewBuilder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        return builder;
    }

    private sealed class FakeTokenProvider : IBackendTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult("managed-identity-token");
    }

    private sealed class NoRetries : IRetryBudget
    {
        public void RecordRequest() { }
        public bool TryAcquireRetry() => false;
    }

    private sealed class RecordingHealth : IHealthState
    {
        private readonly HealthState inner = new(TimeProvider.System);
        private int ttfbCount;
        public int TtfbCount => Volatile.Read(ref ttfbCount);
        public ConcurrentQueue<HealthOutcome> Outcomes { get; } = new();
        public void Reconcile(RoutingTable table, IReadOnlyDictionary<string, ModelHealthOverride> overrides) => inner.Reconcile(table, overrides);
        public IReadOnlyList<DeploymentHealthSnapshot> GetSnapshot() => inner.GetSnapshot();
        public HealthAttempt? TryAcquire(string deploymentId)
        {
            var attempt = inner.TryAcquire(deploymentId);
            if (attempt == null)
                return null;
            Action<HealthOutcome, string?, string?, TimeSpan?> complete = (outcome, milliseconds, retryAfter, ttfb) =>
            {
                Outcomes.Enqueue(outcome);
                attempt.Complete(outcome, milliseconds, retryAfter, ttfb);
            };
            Action<TimeSpan> recordTtfb = ttfb =>
            {
                attempt.RecordTtfb(ttfb);
                Interlocked.Increment(ref ttfbCount);
            };
            return (HealthAttempt)typeof(HealthAttempt).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single().Invoke([complete, recordTtfb]);
        }
    }
}
