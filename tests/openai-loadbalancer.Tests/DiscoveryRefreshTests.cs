using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using openai_loadbalancer.Configuration;
using openai_loadbalancer.Discovery;
using openai_loadbalancer.Health;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Tests;

public class DiscoveryRefreshTests
{
    [Fact]
    public async Task RefreshRetainsHealthOnSuccessAndFailureAndDropsDisappearedIds()
    {
        using var fixture = new Fixture();
        fixture.Arm.Deployments = [Deployment(), Deployment("removed")];
        Assert.True(await fixture.Service.RefreshAsync());
        var id = fixture.State.Current!.Table.Deployments[0].Id;
        fixture.Health.TryAcquire(id)!.Complete(HealthOutcome.Throttled, retryAfter: "120");
        fixture.Arm.Failure = new HttpRequestException("ARM failed");
        Assert.False(await fixture.Service.RefreshAsync());
        Assert.Equal(DeploymentHealth.Throttled, fixture.Health.GetSnapshot().Single(item => item.DeploymentId == id).State);
        fixture.Arm.Failure = null;
        fixture.Arm.Deployments = [Deployment(capacity: 200)];
        Assert.True(await fixture.Service.RefreshAsync());
        Assert.Equal(DeploymentHealth.Throttled, Assert.Single(fixture.Health.GetSnapshot()).State);
        fixture.Arm.Deployments = [];
        Assert.True(await fixture.Service.RefreshAsync());
        Assert.Empty(fixture.Health.GetSnapshot());
    }

    internal const string Subscription = "11111111-1111-1111-1111-111111111111";
    internal static readonly ArmAccount Account = new($"/subscriptions/{Subscription}/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/oai",
        "oai", new Uri("https://oai.openai.azure.com/"), "westeurope");

    internal static DiscoveredDeployment Deployment(string name = "gpt4o", decimal capacity = 100, string version = "2024-11-20") =>
        new(Account.Id, Account.Name, Account.Endpoint, Account.Region, name, new("gpt-4o", version), "Standard", capacity, "Succeeded");

    [Fact]
    public async Task PublishesFirstSnapshotAndKeepsReadinessAndExactSnapshotOnArmFailure()
    {
        using var fixture = new Fixture();
        Assert.False(fixture.State.IsReady);
        Assert.Null(fixture.State.Current);
        Assert.True(await fixture.Service.RefreshAsync());
        var snapshot = fixture.State.Current!;
        Assert.True(fixture.State.IsReady);
        Assert.Equal(100, Assert.Single(snapshot.Table.Deployments).Weight);
        Assert.Equal("orchestrator", Assert.Single(snapshot.Callers).Name);
        Assert.Equal(fixture.Clock.GetUtcNow(), snapshot.RefreshedAt);

        fixture.Arm.Failure = new HttpRequestException("ARM unavailable");
        Assert.False(await fixture.Service.RefreshAsync());
        Assert.Same(snapshot, fixture.State.Current);
        Assert.True(fixture.State.IsReady);
        Assert.Contains(fixture.Logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception == fixture.Arm.Failure);
    }

    [Fact]
    public async Task FirstFailureStaysNotReadyAndLaterRefreshRecovers()
    {
        using var fixture = new Fixture();
        fixture.Arm.Failure = new HttpRequestException("ARM unavailable");
        Assert.False(await fixture.Service.RefreshAsync());
        Assert.False(fixture.State.IsReady);
        fixture.Arm.Failure = null;
        Assert.True(await fixture.Service.RefreshAsync());
        Assert.True(fixture.State.IsReady);
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("deployments")]
    [InlineData("locations")]
    public async Task FailureAtAnyArmStageRetainsLastSnapshot(string stage)
    {
        using var fixture = new Fixture();
        Assert.True(await fixture.Service.RefreshAsync());
        var previous = fixture.State.Current;
        fixture.Arm.FailureStage = stage;
        fixture.Arm.Failure = new HttpRequestException("failed stage");
        Assert.False(await fixture.Service.RefreshAsync());
        Assert.Same(previous, fixture.State.Current);
    }

    [Theory]
    [InlineData("overrides", false)]
    [InlineData("callers", false)]
    [InlineData("overrides", true)]
    [InlineData("callers", true)]
    public async Task InvalidOrMissingFileRetainsBothTableAndCallers(string file, bool missing)
    {
        using var fixture = new Fixture();
        Assert.True(await fixture.Service.RefreshAsync());
        var previous = fixture.State.Current;
        var path = Path.Combine(fixture.DirectoryPath, file + ".yaml");
        if (missing)
            File.Delete(path);
        else
            await File.WriteAllTextAsync(path, "unknownField: true");
        Assert.False(await fixture.Service.RefreshAsync());
        Assert.Same(previous, fixture.State.Current);
        Assert.Contains(fixture.Logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task ReloadsBothFilesAndLogsAddedRemovedAndCapacityChanges()
    {
        using var fixture = new Fixture();
        fixture.Arm.Deployments = [Deployment(), Deployment("removed")];
        Assert.True(await fixture.Service.RefreshAsync());
        var first = fixture.State.Current!;
        fixture.Logger.Entries.Clear();
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        fixture.Arm.Deployments = [Deployment(capacity: 200), Deployment("added", version: "2024-08-06")];
        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryPath, "overrides.yaml"), """
            defaultVersions:
              gpt-4o: "2024-08-06"
            deployments:
              - account: oai
                deployment: gpt4o
                weightMultiplier: 0.5
                disabled: true
            """);
        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryPath, "callers.yaml"), Fixture.CallersYaml("rotated", 'b'));
        Assert.True(await fixture.Service.RefreshAsync());
        var second = fixture.State.Current!;
        Assert.NotSame(first, second);
        Assert.Equal("2024-08-06", second.Table.ResolveModel("gpt-4o").Key?.Version);
        Assert.True(second.Table.Deployments[0].Disabled);
        Assert.Equal(100, second.Table.Deployments[0].Weight);
        Assert.Equal("rotated", Assert.Single(second.Callers).Name);
        Assert.Equal("orchestrator", Assert.Single(first.Callers).Name);
        Assert.Equal(fixture.Clock.GetUtcNow(), second.RefreshedAt);
        Assert.Contains(fixture.Logger.Entries, entry => entry.Message.Contains("Added deployment") && entry.Message.Contains("/added"));
        Assert.Contains(fixture.Logger.Entries, entry => entry.Message.Contains("Removed deployment") && entry.Message.Contains("/removed"));
        Assert.Contains(fixture.Logger.Entries, entry => entry.Message.Contains("capacity changed from 100 to 200"));
    }

    [Fact]
    public async Task UnchangedDeploymentDoesNotLogChanges()
    {
        using var fixture = new Fixture();
        Assert.True(await fixture.Service.RefreshAsync());
        fixture.Logger.Entries.Clear();
        Assert.True(await fixture.Service.RefreshAsync());
        Assert.Single(fixture.Logger.Entries);
        Assert.Contains("refresh succeeded", fixture.Logger.Entries.Single().Message);
    }

    [Fact]
    public async Task EmptySuccessfulDiscoveryIsReadyAndRemovesOldDeployments()
    {
        using var fixture = new Fixture();
        Assert.True(await fixture.Service.RefreshAsync());
        fixture.Arm.Deployments = [];
        Assert.True(await fixture.Service.RefreshAsync());
        Assert.True(fixture.State.IsReady);
        Assert.Empty(fixture.State.Current!.Table.Deployments);
        Assert.Contains(fixture.Logger.Entries, entry => entry.Message.Contains("Removed deployment"));
    }

    [Fact]
    public async Task OverlappingScopesDeduplicateAccountsAndFetchLocationsOncePerSubscription()
    {
        using var fixture = new Fixture(scopes: [Subscription, $"/subscriptions/{Subscription}/resourceGroups/rg", Subscription]);
        Assert.True(await fixture.Service.RefreshAsync());
        Assert.Single(fixture.State.Current!.Table.Deployments);
        Assert.Equal(2, fixture.Arm.AccountScopes.Count);
        Assert.Single(fixture.Arm.LocationSubscriptions);
        Assert.Equal(1, fixture.Arm.DeploymentCalls);
    }

    [Fact]
    public async Task UnknownGeographyDoesNotFailRefreshAndOverrideRestoresDeployment()
    {
        using var fixture = new Fixture();
        fixture.Arm.Locations.Clear();
        Assert.True(await fixture.Service.RefreshAsync());
        Assert.True(fixture.State.IsReady);
        Assert.Empty(fixture.State.Current!.Table.Deployments);
        Assert.Equal(LogLevel.Warning, Assert.Single(fixture.BuilderLogger.Entries).Level);
        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryPath, "overrides.yaml"), "regions:\n  westeurope: { zone: eu }\n");
        Assert.True(await fixture.Service.RefreshAsync());
        Assert.Equal("eu", Assert.Single(fixture.State.Current!.Table.Deployments).Zone);
    }

    [Fact]
    public async Task AliasShadowingDiscoveredModelNameFailsRefresh()
    {
        using var fixture = new Fixture();
        Assert.True(await fixture.Service.RefreshAsync());
        var previous = fixture.State.Current;
        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryPath, "overrides.yaml"), """
            aliases:
              GPT-4o: gpt-4o@2024-11-20
            """);
        Assert.False(await fixture.Service.RefreshAsync());
        Assert.Same(previous, fixture.State.Current);
        Assert.Contains(fixture.Logger.Entries, entry => entry.Level == LogLevel.Error &&
            entry.Exception?.Message.Contains("Aliases cannot equal a discovered model name: GPT-4o") == true);
    }

    [Fact]
    public async Task BuilderFailureKeepsSnapshot()
    {
        using var fixture = new Fixture();
        Assert.True(await fixture.Service.RefreshAsync());
        var previous = fixture.State.Current;
        fixture.Arm.Deployments = [Deployment(capacity: -1)];
        Assert.False(await fixture.Service.RefreshAsync());
        Assert.Same(previous, fixture.State.Current);
    }

    [Fact]
    public async Task PublishesTableAndCallersTogetherOnlyAfterDiscoveryCompletes()
    {
        using var fixture = new Fixture();
        Assert.True(await fixture.Service.RefreshAsync());
        var previous = fixture.State.Current!;
        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryPath, "callers.yaml"), Fixture.CallersYaml("rotated", 'b'));
        fixture.Arm.Deployments = [Deployment(capacity: 200)];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Arm.BeforeDeployments = () => entered.TrySetResult();
        fixture.Arm.DeploymentRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = fixture.Service.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(previous, fixture.State.Current);
        Assert.Equal(100, Assert.Single(previous.Table.Deployments).Weight);
        Assert.Equal("orchestrator", Assert.Single(previous.Callers).Name);
        fixture.Arm.DeploymentRelease.SetResult();
        Assert.True(await refresh);
        var current = fixture.State.Current!;
        Assert.Equal(200, Assert.Single(current.Table.Deployments).Weight);
        Assert.Equal("rotated", Assert.Single(current.Callers).Name);
    }

    [Fact]
    public async Task CancellationDoesNotPublishOrLogFailure()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Arm.BeforeDeployments = () => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.RefreshAsync(cancellation.Token));
        Assert.False(fixture.State.IsReady);
        Assert.Empty(fixture.Logger.Entries);
        fixture.Arm.BeforeDeployments = null;
        Assert.True(await fixture.Service.RefreshAsync());
    }

    [Fact]
    public void SnapshotCopiesCallerLists()
    {
        using var fixture = new Fixture();
        var callers = new YamlConfigurationParser().ParseCallers(Fixture.CallersYaml("caller", 'a'));
        var snapshot = new DiscoverySnapshot(new RoutingTableBuilder().Build([], new(new Dictionary<string, string>()), new()),
            callers, fixture.Clock.GetUtcNow());
        callers.Callers[0].Zones.Clear();
        callers.Callers[0].KeyHashes.Clear();
        callers.Callers.Clear();
        Assert.Equal("eu", Assert.Single(Assert.Single(snapshot.Callers).Zones));
        Assert.Single(snapshot.Callers[0].KeyHashes);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot.Callers[0].Zones).Clear());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-subscription")]
    public async Task InvalidConfigurationDoesNotReportReady(string scope)
    {
        using var fixture = new Fixture(scopes: scope == "" ? [] : [scope]);
        Assert.False(await fixture.Service.RefreshAsync());
        Assert.False(fixture.State.IsReady);
        Assert.Empty(fixture.Arm.AccountScopes);
    }

    internal sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "lb-discovery-" + Guid.NewGuid());
        public FakeArmClient Arm { get; } = new();
        public DiscoveryState State { get; }
        public HealthState Health { get; }
        public TestClock Clock { get; } = new();
        public TestLogger<DiscoveryRefreshService> Logger { get; } = new();
        public TestLogger<RoutingTableBuilder> BuilderLogger { get; } = new();
        public DiscoveryOptions Options { get; }
        public DiscoveryRefreshService Service { get; }

        public Fixture(string[]? scopes = null)
        {
            Health = new(Clock);
            State = new(Health);
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(Path.Combine(DirectoryPath, "overrides.yaml"), "");
            File.WriteAllText(Path.Combine(DirectoryPath, "callers.yaml"), CallersYaml("orchestrator", 'a'));
            Options = new() { Scopes = scopes ?? [Subscription], OverridesFilePath = "overrides.yaml", CallersFilePath = "callers.yaml" };
            Service = new(Arm, new RoutingTableBuilder(BuilderLogger), new YamlConfigurationParser(),
                Microsoft.Extensions.Options.Options.Create(Options), new TestEnvironment { ContentRootPath = DirectoryPath },
                State, Clock, Logger);
        }

        public static string CallersYaml(string name, char hashCharacter) => $$"""
            callers:
              - name: {{name}}
                zones: [eu]
                keyHashes: ["sha256:{{new string(hashCharacter, 64)}}"]
            """;

        public void Dispose()
        {
            Service.Dispose();
            Directory.Delete(DirectoryPath, true);
        }
    }

    internal sealed class FakeArmClient : IArmClient
    {
        public IReadOnlyList<DiscoveredDeployment> Deployments { get; set; } = [Deployment()];
        public Dictionary<string, string> Locations { get; } = new() { ["westeurope"] = "Europe" };
        public List<DiscoveryScope> AccountScopes { get; } = [];
        public List<string> LocationSubscriptions { get; } = [];
        public int DeploymentCalls { get; private set; }
        public Exception? Failure { get; set; }
        public string? FailureStage { get; set; }
        public Action? BeforeDeployments { get; set; }
        public TaskCompletionSource? DeploymentRelease { get; set; }

        private void Check(string stage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure != null && (FailureStage == null || FailureStage == stage))
                throw Failure;
        }

        public Task<IReadOnlyList<ArmAccount>> GetAccountsAsync(DiscoveryScope scope, CancellationToken cancellationToken)
        {
            Check("accounts", cancellationToken);
            AccountScopes.Add(scope);
            return Task.FromResult<IReadOnlyList<ArmAccount>>([Account]);
        }

        public async Task<IReadOnlyList<DiscoveredDeployment>> GetDeploymentsAsync(ArmAccount account, CancellationToken cancellationToken)
        {
            Check("deployments", cancellationToken);
            BeforeDeployments?.Invoke();
            DeploymentCalls++;
            if (DeploymentRelease != null)
                await DeploymentRelease.Task.WaitAsync(cancellationToken);
            return Deployments;
        }

        public Task<IReadOnlyDictionary<string, string>> GetLocationsAsync(string subscriptionId, CancellationToken cancellationToken)
        {
            Check("locations", cancellationToken);
            LocationSubscriptions.Add(subscriptionId);
            return Task.FromResult<IReadOnlyDictionary<string, string>>(Locations);
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
