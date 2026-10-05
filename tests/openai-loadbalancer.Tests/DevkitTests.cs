using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using openai_loadbalancer.Credentials;
using openai_loadbalancer.Devkit;
using openai_loadbalancer.Discovery;
using openai_loadbalancer.Dashboard;
using openai_loadbalancer.Dashboard.Server;
using openai_loadbalancer.Operations;
using openai_loadbalancer.Pipeline;
using Xunit.Abstractions;

namespace openai_loadbalancer.Tests;

public class DevkitTests(ITestOutputHelper output)
{
    private const string Key = "lbk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Sweden = "oai-swedencentral";

    [Fact]
    public async Task FakeAccountsReturnOpenAiChatAndEmbeddingShapesAndTargetHeaders()
    {
        await using var kit = await Kit.StartAsync();
        await kit.SetControls(Sweden, new() { TtfbMs = 0, JitterMs = 0, OutputTokens = 3 });
        using var chat = await kit.Account(Sweden).PostAsJsonAsync("/openai/v1/chat/completions", new { model = "gpt4o" });
        chat.EnsureSuccessStatusCode();
        Assert.Equal(Sweden, Assert.Single(chat.Headers.GetValues("x-devkit-account")));
        Assert.Equal("gpt4o", Assert.Single(chat.Headers.GetValues("x-devkit-deployment")));
        Assert.Equal("100000", Assert.Single(chat.Headers.GetValues("x-ratelimit-remaining-tokens")));
        using var json = await ReadJson(chat);
        Assert.Equal("chat.completion", json.RootElement.GetProperty("object").GetString());
        Assert.StartsWith("chatcmpl-", json.RootElement.GetProperty("id").GetString());
        Assert.Equal("assistant", json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("hello hello hello", json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32());

        using var embeddings = await kit.Account(Sweden).PostAsJsonAsync("/openai/deployments/embeddings/embeddings", new { input = new[] { "one", "two" } });
        embeddings.EnsureSuccessStatusCode();
        using var vectors = await ReadJson(embeddings);
        Assert.Equal("list", vectors.RootElement.GetProperty("object").GetString());
        Assert.Equal("text-embedding-3-large", vectors.RootElement.GetProperty("model").GetString());
        Assert.Equal(2, vectors.RootElement.GetProperty("data").GetArrayLength());
        Assert.Equal(1, vectors.RootElement.GetProperty("data")[1].GetProperty("index").GetInt32());
        Assert.Equal(3, vectors.RootElement.GetProperty("data")[0].GetProperty("embedding").GetArrayLength());
        using var v1Embeddings = await kit.Account(Sweden).PostAsJsonAsync("/openai/v1/embeddings", new { model = "embeddings", input = "one" });
        v1Embeddings.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task TtfbAndJitterDelayResponseHeadersWithinConfiguredRange()
    {
        await using var kit = await Kit.StartAsync();
        // Warm the listener before measuring application delay.
        using (var warmup = await kit.Chat(Sweden)) warmup.EnsureSuccessStatusCode();
        await kit.SetControls(Sweden, new() { TtfbMs = 120, JitterMs = 80 });
        for (var index = 0; index < 3; index++)
        {
            var elapsed = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/openai/deployments/gpt4o/chat/completions")
            {
                Content = JsonContent.Create(new { messages = Array.Empty<object>() })
            };
            using var response = await kit.Account(Sweden).SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            elapsed.Stop();
            response.EnsureSuccessStatusCode();
            Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 110, 1500);
        }
        using var snapshot = await kit.GetJson("/devkit/controls");
        var account = snapshot.RootElement.GetProperty("accounts").EnumerateArray().Single(item => item.GetProperty("name").GetString() == Sweden);
        Assert.Equal(80, account.GetProperty("controls").GetProperty("jitterMs").GetInt32());
    }

    [Fact]
    public async Task StreamingDeliversPacedChunksWithConfiguredTokenCountAndDoneMarker()
    {
        await using var kit = await Kit.StartAsync();
        await kit.SetControls(Sweden, new() { TtfbMs = 0, JitterMs = 0, TokensPerSecond = 5, OutputTokens = 3 });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/openai/v1/chat/completions")
        {
            Content = JsonContent.Create(new { model = "gpt4o", stream = true })
        };
        using var response = await kit.Account(Sweden).SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var elapsed = Stopwatch.StartNew();
        var tokenTimes = new List<TimeSpan>();
        var finished = false;
        var done = false;
        var chunks = 0;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            if (line == "data: [DONE]") { done = true; continue; }
            using var chunk = JsonDocument.Parse(line[6..]);
            Assert.Equal("chat.completion.chunk", chunk.RootElement.GetProperty("object").GetString());
            var choice = chunk.RootElement.GetProperty("choices")[0];
            if (choice.GetProperty("delta").TryGetProperty("content", out var content) && content.GetString() == "hello ")
                tokenTimes.Add(elapsed.Elapsed);
            if (choice.GetProperty("finish_reason").GetString() == "stop") finished = true;
            chunks++;
        }
        Assert.True(done);
        Assert.True(finished);
        Assert.Equal(5, chunks);
        Assert.Equal(3, tokenTimes.Count);
        Assert.True(tokenTimes[^1] - tokenTimes[0] >= TimeSpan.FromMilliseconds(300));
    }

    [Theory]
    [InlineData("error", 500, "InternalServerError")]
    [InlineData("throttle", 429, "RateLimitExceeded")]
    [InlineData("missing", 404, "DeploymentNotFound")]
    public async Task ControlsProduceDeterministicFailures(string control, int status, string code)
    {
        await using var kit = await Kit.StartAsync();
        await kit.SetControls(Sweden, new()
        {
            TtfbMs = 0, JitterMs = 0, ErrorRate = control == "error" ? 1 : 0,
            ThrottleRate = control == "throttle" ? 1 : 0, Missing = control == "missing", RetryAfterMs = 1234
        });
        using var response = await kit.Chat(Sweden);
        Assert.Equal(status, (int)response.StatusCode);
        using var json = await ReadJson(response);
        Assert.Equal(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        if (control == "throttle") Assert.Equal("1234", Assert.Single(response.Headers.GetValues("retry-after-ms")));
    }

    [Fact]
    public async Task RejectsOverlappingErrorAndThrottleSharesWithoutChangingControls()
    {
        await using var kit = await Kit.StartAsync();
        using var response = await kit.Client.PutAsJsonAsync("/devkit/controls", new ControlUpdate(Sweden, null,
            new ControlSettings { ErrorRate = 0.6, ThrottleRate = 0.6 }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var chat = await kit.Chat(Sweden);
        chat.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task UnknownDeploymentReturnsDeploymentNotFound()
    {
        await using var kit = await Kit.StartAsync();
        using var response = await kit.Chat(Sweden, "not-a-deployment");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var json = await ReadJson(response);
        Assert.Equal("DeploymentNotFound", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task DeploymentOverrideIsIndependentAndNullResetRestoresAccountControls()
    {
        await using var kit = await Kit.StartAsync();
        await kit.SetControls(Sweden, new() { TtfbMs = 0, JitterMs = 0, ErrorRate = 1 });
        await kit.SetControls(Sweden, new() { TtfbMs = 0, JitterMs = 0 }, "gpt4o");
        using (var chat = await kit.Chat(Sweden)) chat.EnsureSuccessStatusCode();
        using (var embedding = await kit.Account(Sweden).PostAsJsonAsync("/openai/v1/embeddings", new { model = "embeddings", input = "one" }))
            Assert.Equal(HttpStatusCode.InternalServerError, embedding.StatusCode);
        await kit.SetControls(Sweden, null, "gpt4o");
        using var reset = await kit.Chat(Sweden);
        Assert.Equal(HttpStatusCode.InternalServerError, reset.StatusCode);
    }

    [Fact]
    public async Task OutageStopsListenerAndRecoverAllRestoresTheSameEndpoint()
    {
        await using var kit = await Kit.StartAsync();
        var endpoint = kit.Listeners.Endpoint("oai-eastus2");
        await kit.SetControls("oai-eastus2", new() { TtfbMs = 0, JitterMs = 0, OutputTokens = 100, TokensPerSecond = 1 });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/openai/v1/chat/completions")
        {
            Content = JsonContent.Create(new { model = "gpt4o", stream = true })
        };
        using var active = await kit.Account("oai-eastus2").SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        active.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await active.Content.ReadAsStreamAsync());
        Assert.StartsWith("data: ", await reader.ReadLineAsync());
        var shutdown = Stopwatch.StartNew();
        using (var preset = await kit.Client.PostAsync("/devkit/presets/eastus2-outage", null)) preset.EnsureSuccessStatusCode();
        Assert.True(shutdown.Elapsed < TimeSpan.FromSeconds(5), "An outage must stop the listener while a long stream is active.");
        await Assert.ThrowsAnyAsync<IOException>(() => reader.ReadToEndAsync());
        await Assert.ThrowsAsync<HttpRequestException>(() => kit.Chat("oai-eastus2"));
        using (var recover = await kit.Client.PostAsync("/devkit/presets/recover-all", null)) recover.EnsureSuccessStatusCode();
        Assert.Equal(endpoint, kit.Listeners.Endpoint("oai-eastus2"));
        using var recovered = await kit.Chat("oai-eastus2");
        recovered.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task MockArmSupportsRealClientAndRuntimeDeploymentChanges()
    {
        await using var kit = await Kit.StartAsync();
        using var http = new HttpClient();
        var arm = new ArmClient(http, new FakeTokenCredential(), Options.Create(new DiscoveryOptions { ArmEndpoint = kit.Client.BaseAddress!.GetLeftPart(UriPartial.Authority) }));
        var accounts = await arm.GetAccountsAsync(DiscoveryScope.Parse(kit.Scenario.Subscription), CancellationToken.None);
        Assert.Equal(5, accounts.Count);
        var grouped = await arm.GetAccountsAsync(DiscoveryScope.Parse($"/subscriptions/{kit.Scenario.Subscription}/resourceGroups/devkit"), CancellationToken.None);
        Assert.Equal(accounts.Select(item => item.Id), grouped.Select(item => item.Id));
        Assert.All(accounts, account => Assert.Equal(kit.Listeners.Endpoint(account.Name), account.Endpoint));
        var locations = await arm.GetLocationsAsync(kit.Scenario.Subscription, CancellationToken.None);
        Assert.Equal("Europe", locations["swedencentral"]);
        Assert.Equal("United States", locations["eastus2"]);
        var account = accounts.Single(item => item.Name == Sweden);
        var deployments = await arm.GetDeploymentsAsync(account, CancellationToken.None);
        Assert.Equal(2, deployments.Count);
        Assert.Contains(deployments, item => item.Model.ToString() == "gpt-4o@2024-11-20" && item.Sku == "Standard" && item.Capacity == 120);
        var added = new DeploymentDefinition { Name = "extra", Model = "gpt-4o", Version = "2024-11-20", Sku = "DataZoneStandard", Capacity = 77 };
        using (var put = await kit.Client.PutAsJsonAsync($"/devkit/deployments/{Sweden}/extra", added)) put.EnsureSuccessStatusCode();
        Assert.Contains(await arm.GetDeploymentsAsync(account, CancellationToken.None), item => item.DeploymentName == "extra" && item.Capacity == 77);
        using (var removed = await kit.Client.DeleteAsync($"/devkit/deployments/{Sweden}/extra")) removed.EnsureSuccessStatusCode();
        Assert.DoesNotContain(await arm.GetDeploymentsAsync(account, CancellationToken.None), item => item.DeploymentName == "extra");
        using var unsupported = await kit.Client.GetAsync($"/subscriptions/{kit.Scenario.Subscription}/locations?api-version=wrong");
        Assert.Equal(HttpStatusCode.NotFound, unsupported.StatusCode);
    }

    [Fact]
    public async Task RealLbRoutesAroundEastusOutageOpensAccountAndRefreshesChangedDeployments()
    {
        await using var kit = await Kit.StartAsync();
        await using var lb = await StartLb(kit);
        using var client = new HttpClient { BaseAddress = Address(lb) };
        client.DefaultRequestHeaders.Add("api-key", Key);
        client.DefaultRequestHeaders.Add("x-lb-data-zone", "global");
        using (var ready = await client.GetAsync("/readyz")) ready.EnsureSuccessStatusCode();
        using (var before = await client.PostAsJsonAsync("/openai/v1/chat/completions", new { model = "gpt-4o" }))
        {
            before.EnsureSuccessStatusCode();
            Assert.Equal("oai-eastus2", Assert.Single(before.Headers.GetValues("x-devkit-account")));
        }
        using (var outage = await kit.Client.PostAsync("/devkit/presets/eastus2-outage", null)) outage.EnsureSuccessStatusCode();
        using (var after = await client.PostAsJsonAsync("/openai/v1/chat/completions", new { model = "gpt-4o" }))
        {
            after.EnsureSuccessStatusCode();
            Assert.NotEqual("oai-eastus2", Assert.Single(after.Headers.GetValues("x-devkit-account")));
        }
        await WaitUntil(() => lb.Services.GetRequiredService<RequestHistory>().GetRecent(10).Count == 2);
        using (var history = await ReadJson(await client.GetAsync("/admin/requests")))
        {
            var attempts = history.RootElement[0].GetProperty("attempts").EnumerateArray().ToArray();
            Assert.Equal("eastus2", attempts[0].GetProperty("region").GetString());
            Assert.Equal("AccountFailure", attempts[0].GetProperty("healthOutcome").GetString());
            Assert.Equal("account_failure", attempts[0].GetProperty("retryReason").GetString());
            Assert.NotEqual("eastus2", attempts[^1].GetProperty("region").GetString());
            Assert.Equal(200, attempts[^1].GetProperty("status").GetInt32());
        }
        using (var state = await ReadJson(await client.GetAsync("/admin/state")))
        {
            var eastus = state.RootElement.GetProperty("deployments").EnumerateArray().Where(item => item.GetProperty("region").GetString() == "eastus2").ToArray();
            Assert.Equal(3, eastus.Length);
            Assert.All(eastus, deployment => Assert.True(deployment.GetProperty("health").GetProperty("accountOpen").GetBoolean()));
        }
        var discovery = lb.Services.GetRequiredService<DiscoveryState>();
        var extra = new DeploymentDefinition { Name = "refresh-test", Model = "gpt-4o", Version = "2024-11-20", Capacity = 25 };
        using (var put = await kit.Client.PutAsJsonAsync($"/devkit/deployments/{Sweden}/{extra.Name}", extra)) put.EnsureSuccessStatusCode();
        await WaitUntil(() => discovery.Current!.Table.Deployments.Any(item => item.DeploymentName == extra.Name));
        using (var remove = await kit.Client.DeleteAsync($"/devkit/deployments/{Sweden}/{extra.Name}")) remove.EnsureSuccessStatusCode();
        await WaitUntil(() => discovery.Current!.Table.Deployments.All(item => item.DeploymentName != extra.Name));
        // Generated traffic supplies the caller key and must preserve streaming and the zone header.
        kit.App.Services.GetRequiredService<LbClient>().Http.BaseAddress = client.BaseAddress;
        var traffic = new TrafficSettings { Enabled = true, RequestsPerSecond = 10, Concurrency = 2, Model = "gpt-4o", StreamingShare = 1, Zone = "eu" };
        foreach (var account in kit.Scenario.Accounts)
            if (account.Region != "eastus2") await kit.SetControls(account.Name, new() { TtfbMs = 0, JitterMs = 0, OutputTokens = 1, TokensPerSecond = 100 });
        using (var start = await kit.Client.PutAsJsonAsync("/devkit/traffic", traffic)) start.EnsureSuccessStatusCode();
        await WaitUntil(() => lb.Services.GetRequiredService<RequestHistory>().GetRecent(10).Any(item => item.Streaming && item.Zone == "eu" && item.Caller == "devkit" && item.Status == 200));
        using (var stop = await kit.Client.PutAsJsonAsync("/devkit/traffic", traffic with { Enabled = false })) stop.EnsureSuccessStatusCode();
        await WaitUntil(async () =>
        {
            using var status = await kit.GetJson("/devkit/traffic");
            return status.RootElement.GetProperty("inFlight").GetInt32() == 0;
        });
        using var stopped = await kit.GetJson("/devkit/traffic");
        var sent = stopped.RootElement.GetProperty("sent").GetInt64();
        await Task.Delay(150);
        using var later = await kit.GetJson("/devkit/traffic");
        Assert.Equal(sent, later.RootElement.GetProperty("sent").GetInt64());
        Assert.False(later.RootElement.GetProperty("enabled").GetBoolean());
        await lb.StopAsync();
    }

    [Fact]
    public async Task ControlPanelLinksToTheDashboardAndWorksWhenLbIsUnreachable()
    {
        await using var kit = await Kit.StartAsync();
        using var page = await kit.Client.GetAsync("/");
        page.EnsureSuccessStatusCode();
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("href=\"http://localhost:5200\"", await page.Content.ReadAsStringAsync());
        using var controls = await kit.GetJson("/devkit/controls");
        Assert.Equal(5, controls.RootElement.GetProperty("accounts").GetArrayLength());
    }

    [Fact]
    public async Task DashboardStreamDeliversMergedStateAndCompletedRequestsDuringDevkitTraffic()
    {
        await using var dashboard = DashboardHost.Create(["--environment", "Development"], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration["Dashboard:DevAuth"] = "true";
        });
        await dashboard.StartAsync();
        await using var kit = await Kit.StartAsync();
        await using var lb = await StartLb(kit, new Uri(Address(dashboard), "/ingest").AbsoluteUri);
        kit.App.Services.GetRequiredService<LbClient>().Http.BaseAddress = Address(lb);
        foreach (var account in kit.Scenario.Accounts)
            await kit.SetControls(account.Name, new() { TtfbMs = 0, JitterMs = 0, OutputTokens = 1, TokensPerSecond = 100 });
        using var client = new HttpClient { BaseAddress = Address(dashboard) };
        using var response = await client.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal("event: snapshot", await reader.ReadLineAsync());
        Assert.StartsWith("data: ", await reader.ReadLineAsync());
        Assert.Equal("", await reader.ReadLineAsync());
        var traffic = new TrafficSettings { Enabled = true, RequestsPerSecond = 10, Concurrency = 4, StreamingShare = 1, Zone = "eu" };
        try
        {
            using var started = await kit.Client.PutAsJsonAsync("/devkit/traffic", traffic);
            started.EnsureSuccessStatusCode();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                Assert.Equal("event: delta", await reader.ReadLineAsync(deadline.Token));
                var data = await reader.ReadLineAsync(deadline.Token);
                Assert.StartsWith("data: ", data);
                Assert.Equal("", await reader.ReadLineAsync(deadline.Token));
                using var frame = JsonDocument.Parse(data![6..]);
                var root = frame.RootElement;
                if (root.GetProperty("requests").GetArrayLength() == 0)
                    continue;
                Assert.NotEmpty(root.GetProperty("deployments").EnumerateArray());
                var replica = Assert.Single(root.GetProperty("replicas").EnumerateArray()).GetString();
                Assert.Equal(DashboardPublisher.Replica, replica);
                Assert.All(root.GetProperty("deployments").EnumerateArray(), deployment =>
                    Assert.Equal(replica, Assert.Single(deployment.GetProperty("replicas").EnumerateArray()).GetProperty("replica").GetString()));
                Assert.All(root.GetProperty("requests").EnumerateArray(), request =>
                {
                    Assert.Equal("devkit", request.GetProperty("caller").GetString());
                    Assert.Equal(200, request.GetProperty("status").GetInt32());
                    Assert.True(request.GetProperty("streaming").GetBoolean());
                    Assert.Equal("eu", request.GetProperty("zone").GetString());
                    Assert.NotNull(Assert.Single(request.GetProperty("attempts").EnumerateArray()).GetProperty("deploymentId").GetString());
                });
                Assert.NotEmpty(root.GetProperty("counts").EnumerateArray());
                output.WriteLine($"Live SSE smoke: dashboard={Address(dashboard)}, devkit={kit.Client.BaseAddress}, LB={Address(lb)}");
                output.WriteLine($"Merged deployments={root.GetProperty("deployments").GetArrayLength()}, replicas=1, completed streaming requests={root.GetProperty("requests").GetArrayLength()}, count groups={root.GetProperty("counts").GetArrayLength()}");
                output.WriteLine(data);
                break;
            }
        }
        finally
        {
            using var stopped = await kit.Client.PutAsJsonAsync("/devkit/traffic", traffic with { Enabled = false });
            stopped.EnsureSuccessStatusCode();
            await lb.StopAsync();
            await kit.App.StopAsync();
            response.Dispose();
            await dashboard.StopAsync();
            output.WriteLine("Stopped dashboard, LB, devkit, traffic generator, and all fake account listeners.");
        }
    }

    private static async Task<WebApplication> StartLb(Kit kit, string? dashboardUrl = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = kit.ConfigDirectory, EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Discovery:Scopes:0"] = kit.Scenario.Subscription,
            ["Discovery:ArmEndpoint"] = kit.Client.BaseAddress!.GetLeftPart(UriPartial.Authority),
            ["Discovery:RefreshInterval"] = "00:00:00.100",
            ["Discovery:CallersFilePath"] = Path.Combine(kit.ConfigDirectory, "callers.yaml"),
            ["Discovery:OverridesFilePath"] = Path.Combine(kit.ConfigDirectory, "overrides.yaml"),
            ["Azure:Credential"] = "Fake",
            ["Dashboard:IngestUrl"] = dashboardUrl,
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = ""
        });
        builder.Services.AddOperations(builder.Configuration);
        builder.Services.AddDiscovery(builder.Configuration);
        builder.Services.AddDashboardPublisher(builder.Configuration);
        builder.Services.AddRequestPipeline(builder.Configuration);
        var app = builder.Build();
        app.MapDiscoveryReadiness();
        app.MapAdminState();
        app.MapAdminRequests();
        app.MapRequestPipeline();
        try
        {
            await app.StartAsync();
            await WaitUntil(() => app.Services.GetRequiredService<DiscoveryState>().IsReady);
            return app;
        }
        catch { await app.DisposeAsync(); throw; }
    }

    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    private static Task WaitUntil(Func<bool> condition) => WaitUntil(() => Task.FromResult(condition()));
    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition()) await Task.Delay(20, deadline.Token);
    }

    private sealed class Kit(WebApplication app, Scenario scenario, string configDirectory) : IAsyncDisposable
    {
        private readonly Dictionary<string, HttpClient> accounts = [];
        public WebApplication App => app;
        public Scenario Scenario => scenario;
        public string ConfigDirectory => configDirectory;
        public AccountListeners Listeners => app.Services.GetRequiredService<AccountListeners>();
        public HttpClient Client { get; } = new() { BaseAddress = Address(app) };

        public static async Task<Kit> StartAsync()
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/devkit"));
            var scenario = Scenario.Load(Path.Combine(root, "config/scenario.yaml"));
            scenario = scenario with { Accounts = scenario.Accounts.Select(account => account with { Port = 0 }).ToList() };
            var directory = Path.Combine(Path.GetTempPath(), "devkit-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            foreach (var name in new[] { "callers.yaml", "overrides.yaml" })
                File.Copy(Path.Combine(root, "config", name), Path.Combine(directory, name));
            // Port zero is never a running LB. This also avoids selecting a fixed test port.
            var app = DevkitHost.Create(scenario, root, "http://127.0.0.1:0", Key, port: 0);
            try { await app.StartAsync(); return new(app, scenario, directory); }
            catch { await app.DisposeAsync(); Directory.Delete(directory, true); throw; }
        }

        public HttpClient Account(string name)
        {
            if (!accounts.TryGetValue(name, out var client))
                accounts[name] = client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = Listeners.Endpoint(name), Timeout = TimeSpan.FromSeconds(5) };
            return client;
        }
        public Task<HttpResponseMessage> Chat(string account, string deployment = "gpt4o") =>
            Account(account).PostAsJsonAsync($"/openai/deployments/{deployment}/chat/completions", new { messages = Array.Empty<object>() });
        public async Task SetControls(string account, ControlSettings? controls, string? deployment = null)
        {
            using var response = await Client.PutAsJsonAsync("/devkit/controls", new ControlUpdate(account, deployment, controls));
            response.EnsureSuccessStatusCode();
        }
        public async Task<JsonDocument> GetJson(string path)
        {
            using var response = await Client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            return await ReadJson(response);
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            foreach (var client in accounts.Values) client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            Directory.Delete(configDirectory, true);
        }
    }
}
