using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using openai_loadbalancer.Configuration;
using openai_loadbalancer.Discovery;
using openai_loadbalancer.Health;
using openai_loadbalancer.Operations;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Tests;

public class RequestHistoryTests
{
    private const string Key = "lbk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void EmptyHistoryReturnsEmptySnapshot() => Assert.Empty(new RequestHistory().GetRecent(100));

    [Fact]
    public void RingRetainsOnlyLastFiveHundredRequests()
    {
        var history = new RequestHistory();
        for (var index = 0; index < 750; index++)
            history.Add(Record(index));
        var records = history.GetRecent(1000);
        Assert.Equal(500, records.Count);
        Assert.Equal("749", records[0].Id);
        Assert.Equal("250", records[^1].Id);
        Assert.Equal(100, history.GetRecent(100).Count);
    }

    [Fact]
    public void RequestsSortByStartTimeWhenTheyCompleteOutOfOrder()
    {
        var history = new RequestHistory();
        history.Add(Record(3));
        history.Add(Record(1));
        history.Add(Record(2));
        Assert.Equal(["3", "2", "1"], history.GetRecent(3).Select(record => record.Id));
    }

    [Fact]
    public void RetainedModelMetadataHasBoundedLength()
    {
        var history = new RequestHistory();
        history.Add(Record(1) with { RequestedModel = new string('m', 16 * 1024 * 1024) });
        var model = Assert.Single(history.GetRecent(1)).RequestedModel;
        Assert.Equal(new string('m', RequestHistory.MaximumModelLength) + "…", model);
    }

    [Fact]
    public void StoredAttemptsAndReturnedSnapshotsCannotChange()
    {
        var attempts = new List<RequestAttemptRecord> { new("primary", "sweden", "swedencentral", 0, 429, 20, "Throttled", "throttled") };
        var history = new RequestHistory();
        history.Add(Record(1) with { Attempts = attempts });
        var snapshot = history.GetRecent(100);
        attempts.Clear();
        Assert.Single(snapshot[0].Attempts);
        Assert.Throws<NotSupportedException>(() => ((IList<RequestAttemptRecord>)snapshot[0].Attempts).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<RequestRecord>)snapshot).Clear());
        for (var index = 2; index < 600; index++)
            history.Add(Record(index));
        Assert.Equal("1", Assert.Single(snapshot).Id);
    }

    [Fact]
    public async Task ConcurrentWritesAndReadsKeepBoundedCompleteRecords()
    {
        var history = new RequestHistory();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 200; index++)
            {
                history.Add(Record(worker * 200 + index));
                var snapshot = history.GetRecent(500);
                Assert.InRange(snapshot.Count, 1, 500);
                Assert.All(snapshot, record => Assert.Single(record.Attempts));
                Assert.Equal(snapshot.Count, snapshot.Select(record => record.Id).Distinct().Count());
            }
        })));
        Assert.Equal(500, history.GetRecent(500).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void StoreRejectsNonpositiveLimit(int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequestHistory().GetRecent(limit));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndpointRequiresCallerKeyAndReturnsSafeMetadata(bool bearer)
    {
        await using var server = await Server.StartAsync();
        server.History.Add(Record(1));
        using var anonymous = await server.Client.GetAsync("/admin/requests");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var request = AuthorizedRequest("/admin/requests", bearer);
        using var response = await server.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Key, text);
        Assert.DoesNotContain("sha256:", text);
        Assert.DoesNotContain("body", text, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(text);
        var record = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("1", record.GetProperty("id").GetString());
        Assert.Equal("dev-caller", record.GetProperty("caller").GetString());
        Assert.Equal("gpt-4o@2024-11-20", record.GetProperty("modelKey").GetString());
        Assert.Equal(200, record.GetProperty("status").GetInt32());
        var attempt = Assert.Single(record.GetProperty("attempts").EnumerateArray());
        Assert.Equal("swedencentral", attempt.GetProperty("region").GetString());
        Assert.Equal(10, attempt.GetProperty("ttfbMs").GetDouble());
    }

    [Fact]
    public async Task EndpointRejectsUnknownCallerKey()
    {
        await using var server = await Server.StartAsync();
        using var request = AuthorizedRequest("/admin/requests");
        request.Headers.Remove("api-key");
        request.Headers.Add("api-key", "lbk_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBA");
        using var response = await server.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EndpointReturnsUnavailableBeforeFirstDiscoverySnapshot()
    {
        await using var server = await Server.StartAsync(publishSnapshot: false);
        using var request = AuthorizedRequest("/admin/requests");
        using var response = await server.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("", 100)]
    [InlineData("?limit=3", 3)]
    [InlineData("?limit=500", 500)]
    [InlineData("?limit=999", 500)]
    public async Task EndpointUsesDefaultLimitAndClampsToCapacity(string query, int expected)
    {
        await using var server = await Server.StartAsync();
        for (var index = 0; index < 510; index++)
            server.History.Add(Record(index));
        using var request = AuthorizedRequest("/admin/requests" + query);
        using var response = await server.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, json.RootElement.GetArrayLength());
        Assert.Equal("509", json.RootElement[0].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("?limit=")]
    [InlineData("?limit=0")]
    [InlineData("?limit=-1")]
    [InlineData("?limit=abc")]
    [InlineData("?limit=1.5")]
    [InlineData("?limit=2147483648")]
    [InlineData("?limit=1&limit=2")]
    public async Task EndpointRejectsInvalidLimit(string query)
    {
        await using var server = await Server.StartAsync();
        using var request = AuthorizedRequest("/admin/requests" + query);
        using var response = await server.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private static RequestRecord Record(int index) => new(index.ToString(), DateTimeOffset.UnixEpoch.AddSeconds(index),
        "dev-caller", "gpt-4o", "gpt-4o@2024-11-20", "eu", false, 200, 30,
        [new("gpt4o", "sweden", "swedencentral", 1, 200, 10, "Success", null)], "success");

    private static HttpRequestMessage AuthorizedRequest(string path, bool bearer = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer)
            request.Headers.Authorization = new("Bearer", Key);
        else
            request.Headers.Add("api-key", Key);
        return request;
    }

    private sealed class Server(WebApplication app, RequestHistory history) : IAsyncDisposable
    {
        public RequestHistory History => history;
        public HttpClient Client { get; } = new() { BaseAddress = new Uri(app.Urls.Single()) };

        public static async Task<Server> StartAsync(bool publishSnapshot = true)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddSingleton<IHealthState, HealthState>();
            builder.Services.AddSingleton<DiscoveryState>();
            builder.Services.AddSingleton<RequestHistory>();
            var app = builder.Build();
            if (publishSnapshot)
            {
                var table = new RoutingTableBuilder().Build([], new RegionGeography(new Dictionary<string, string>()), new());
                var callers = new CallersConfiguration
                {
                    Callers = [new Caller
                    {
                        Name = "dev-caller", Zones = ["eu"],
                        KeyHashes = ["sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key))).ToLowerInvariant()]
                    }]
                };
                typeof(DiscoveryState).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(app.Services.GetRequiredService<DiscoveryState>(),
                        [new DiscoverySnapshot(table, callers, DateTimeOffset.UtcNow), new RoutingOverrides()]);
            }
            app.MapAdminRequests();
            await app.StartAsync();
            return new(app, app.Services.GetRequiredService<RequestHistory>());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
