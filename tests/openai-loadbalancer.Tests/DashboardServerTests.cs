using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using openai_loadbalancer.Dashboard;
using openai_loadbalancer.Dashboard.Server;

namespace openai_loadbalancer.Tests;

public class DashboardServerTests
{
    private const string LbId = "00000000-0000-0000-0000-000000000001";
    private const string OtherId = "00000000-0000-0000-0000-000000000002";

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task DevAuthFailsStartupOutsideDevelopment(string environment)
    {
        await using var app = Create(environment, true);
        await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());
    }

    [Fact]
    public async Task ProductionRequiresLbIdentityConfiguration()
    {
        await using var app = Create("Production", false, lbId: "");
        await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());
    }

    [Theory]
    [InlineData(null, 403)]
    [InlineData("Bearer wrong", 403)]
    [InlineData("Basic devkit-token", 403)]
    [InlineData("Bearer devkit-token", 202)]
    public async Task DevelopmentIngestRequiresStaticBearerToken(string? authorization, int expected)
    {
        await using var app = Create("Development", true);
        await app.StartAsync();
        using var client = Client(app);
        using var request = Ingest(authorization);
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
        using var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Theory]
    [InlineData(null, "Bearer token", 403)]
    [InlineData("malformed", "Bearer token", 403)]
    [InlineData(OtherId, "Bearer token", 403)]
    [InlineData(LbId, null, 403)]
    [InlineData(LbId, "Bearer token", 202)]
    public async Task ProductionIngestRequiresEasyAuthLbOidAndBearer(string? identity, string? authorization, int expected)
    {
        await using var app = Create("Production", false);
        await app.StartAsync();
        using var client = Client(app);
        using var request = Ingest(authorization);
        if (identity != null)
            request.Headers.Add("X-MS-CLIENT-PRINCIPAL", identity == "malformed" ? identity : Principal(identity));
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
    }

    [Theory]
    [InlineData("/ingest/")]
    [InlineData("/INGEST")]
    public async Task IngestRouteVariantsCannotBypassLbIdentityGuard(string path)
    {
        await using var app = Create("Production", false);
        await app.StartAsync();
        using var client = Client(app);
        using var request = Ingest("Bearer token");
        request.RequestUri = new Uri(path, UriKind.Relative);
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL", Principal(OtherId));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task BrowserRequiresLoginButAcceptsAnyTenantUserAndHealthIsAnonymous()
    {
        await using var app = Create("Production", false);
        await app.StartAsync();
        using var client = Client(app);
        using var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var anonymous = await client.GetAsync("/api/stream");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.StartsWith("/.auth/login/aad", anonymous.Headers.Location?.OriginalString);
        using var browser = new HttpRequestMessage(HttpMethod.Get, "/api/stream");
        browser.Headers.Add("X-MS-CLIENT-PRINCIPAL", Principal(OtherId));
        using var response = await client.SendAsync(browser, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"replica\":\"one\",\"state\":null,\"requests\":[],\"counts\":[]}")]
    [InlineData("not json")]
    [InlineData("{\"replica\":\"one\",\"state\":{\"deployments\":[]},\"requests\":[],\"counts\":[],\"routes\":[null]}")]
    [InlineData("{\"replica\":\"one\",\"state\":{\"deployments\":[]},\"requests\":[],\"counts\":[],\"routes\":[{\"status\":200,\"hops\":[{\"deploymentId\":\"\",\"outcome\":\"Success\"}],\"count\":1}]}")]
    [InlineData("{\"replica\":\"one\",\"state\":{\"deployments\":[]},\"requests\":[],\"counts\":[],\"routes\":[{\"status\":200,\"hops\":[],\"count\":-1}]}")]
    public async Task IngestRejectsIncompleteOrMalformedBatches(string body)
    {
        await using var app = Create("Development", true);
        await app.StartAsync();
        using var client = Client(app);
        using var request = Ingest("Bearer devkit-token");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StreamSendsInitialSnapshotThenMergedDeltaWithRequestEventsAndCounts()
    {
        var clock = new TestClock();
        await using var app = Create("Development", true, clock);
        await app.StartAsync();
        using var client = Client(app);
        using var response = await client.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var snapshot = await Frame(reader);
        Assert.Equal("event: snapshot", snapshot.Kind);
        using var first = JsonDocument.Parse(snapshot.Data);
        Assert.Empty(first.RootElement.GetProperty("deployments").EnumerateArray());
        using var one = Ingest("Bearer devkit-token");
        one.Content = JsonContent.Create(DashboardStoreTests.Batch(clock, "one", "Healthy"));
        using var two = Ingest("Bearer devkit-token");
        two.Content = JsonContent.Create(DashboardStoreTests.Batch(clock, "two", "Open") with
        {
            Requests = [DashboardStoreTests.Request("observed")], Counts = [new("deployment", "Success", 250)],
            Routes = [new("dev", "gpt-4o@1", "eu", 200, [new("deployment", "Success")], 250)]
        });
        using var acceptedOne = await client.SendAsync(one);
        using var acceptedTwo = await client.SendAsync(two);
        acceptedOne.EnsureSuccessStatusCode();
        acceptedTwo.EnsureSuccessStatusCode();
        clock.Advance(TimeSpan.FromSeconds(1));
        var delta = await Frame(reader);
        Assert.Equal("event: delta", delta.Kind);
        using var json = JsonDocument.Parse(delta.Data);
        var deployment = Assert.Single(json.RootElement.GetProperty("deployments").EnumerateArray());
        Assert.Equal("Open", deployment.GetProperty("deployment").GetProperty("state").GetString());
        Assert.Equal(2, deployment.GetProperty("replicas").GetArrayLength());
        Assert.Equal("observed", Assert.Single(json.RootElement.GetProperty("requests").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(250, Assert.Single(json.RootElement.GetProperty("counts").EnumerateArray()).GetProperty("requestsPerSecond").GetDouble());
        Assert.Equal(250, Assert.Single(json.RootElement.GetProperty("routes").EnumerateArray()).GetProperty("count").GetInt64());
        Assert.Empty(json.RootElement.GetProperty("routeHistory").EnumerateArray());
        Assert.DoesNotContain("devkit-token", delta.Data);
    }

    private static WebApplication Create(string environment, bool devAuth, TimeProvider? clock = null, string lbId = LbId) =>
        DashboardHost.Create(["--environment", environment], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration["Dashboard:DevAuth"] = devAuth.ToString();
            builder.Configuration["Dashboard:LbIdentityObjectId"] = lbId;
            if (clock != null)
                builder.Services.AddSingleton(clock);
        });

    private static HttpClient Client(WebApplication app) => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(5)
    };

    private static HttpRequestMessage Ingest(string? authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/ingest")
        {
            Content = JsonContent.Create(DashboardStoreTests.Batch(TimeProvider.System, "one", "Healthy"))
        };
        if (authorization != null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    private static string Principal(string id) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        auth_typ = "aad", claims = new[] { new { typ = "http://schemas.microsoft.com/identity/claims/objectidentifier", val = id } }
    })));

    private static async Task<(string Kind, string Data)> Frame(StreamReader reader)
    {
        var kind = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var data = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.StartsWith("data: ", data);
        return (kind!, data![6..]);
    }
}
