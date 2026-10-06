using openai_loadbalancer.Configuration;
using openai_loadbalancer.Health;
using openai_loadbalancer.Pipeline;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Tests;

public class SelectionTests
{
    private static readonly ModelKey Model = new("gpt-4o", "2024-11-20");

    [Fact]
    public void SelectsOnlyTheRequestedModelZoneAndUntriedEnabledPositiveWeightDeployment()
    {
        var fixture = new Fixture(
            Source("wrong-model") with { Model = new("gpt-4o", "other") },
            Source("us") with { Region = "eastus" }, Source("global", sku: "GlobalStandard"),
            Source("zero", weight: 0), Source("disabled"), Source("tried"), Source("eu"));
        fixture.Overrides.Deployments.Add(new() { Account = "disabled", Deployment = "disabled", Disabled = true });
        fixture.Refresh();
        var result = fixture.Select(tried: [fixture.Id("tried")]);
        using var attempt = result.Attempt;
        Assert.Equal("eu", result.Deployment?.DeploymentName);
        Assert.NotNull(attempt);
        Assert.Null(result.RetryAfter);
    }

    [Theory]
    [InlineData("Standard", "eu")]
    [InlineData("Standard", "us")]
    [InlineData("GlobalStandard", "global")]
    public void GlobalZoneAllowsEveryDeploymentZone(string sku, string expectedZone)
    {
        var fixture = new Fixture(Source("only", sku: sku) with { Region = expectedZone == "us" ? "eastus" : "westeurope" });
        var result = fixture.Select("global");
        using var attempt = result.Attempt;
        Assert.Equal(expectedZone, result.Deployment?.Zone);
    }

    [Fact]
    public void SelectsTheLowestHealthyTierBeforeHigherTiers()
    {
        var fixture = new Fixture(Source("tier2", sku: "GlobalStandard"), Source("tier1"),
            Source("tier0", sku: "ProvisionedManaged"));
        var first = fixture.Select("global");
        first.Attempt!.Dispose();
        Assert.Equal("tier0", first.Deployment?.DeploymentName);
        var second = fixture.Select("global", [fixture.Id("tier0")]);
        second.Attempt!.Dispose();
        Assert.Equal("tier1", second.Deployment?.DeploymentName);
        var third = fixture.Select("global", [fixture.Id("tier0"), fixture.Id("tier1")]);
        third.Attempt!.Dispose();
        Assert.Equal("tier2", third.Deployment?.DeploymentName);
    }

    [Theory]
    [InlineData(0, "small")]
    [InlineData(0.2499, "small")]
    [InlineData(0.25, "large")]
    [InlineData(0.9999, "large")]
    public void ChoosesWithinTheTierInProportionToWeight(double draw, string expected)
    {
        var fixture = new Fixture(Source("small", weight: 1), Source("large", weight: 3));
        fixture.Random.Draw = draw;
        var result = fixture.Select();
        using var attempt = result.Attempt;
        Assert.Equal(expected, result.Deployment?.DeploymentName);
    }

    [Theory]
    [InlineData(0.0499, "slow")]
    [InlineData(0.05, "healthy")]
    [InlineData(0.9999, "healthy")]
    public void FivePercentOfDrawsProbeDegradedBeforeTheHealthyTier(double draw, string expected)
    {
        var fixture = new Fixture(Source("slow", sku: "ProvisionedManaged"), Source("healthy"));
        fixture.Sample("healthy", 1);
        fixture.Sample("slow", 4);
        fixture.Random.Draw = draw;
        var result = fixture.Select();
        using var attempt = result.Attempt;
        Assert.Equal(expected, result.Deployment?.DeploymentName);
    }

    [Fact]
    public void RetriesPreferHealthyDeploymentsInsteadOfTakingAnotherDegradedProbeLottery()
    {
        var fixture = new Fixture(Source("slow", sku: "ProvisionedManaged"), Source("healthy"), Source("tried"));
        fixture.Sample("healthy", 1);
        fixture.Sample("slow", 4);
        fixture.Random.Draw = 0;
        var result = fixture.Select(tried: [fixture.Id("tried")]);
        using var attempt = result.Attempt;
        Assert.Equal("healthy", result.Deployment?.DeploymentName);
    }

    [Fact]
    public void DegradedFallbackUsesTheLowestTierWhenNoHealthyDeploymentRemains()
    {
        var fixture = new Fixture(Source("slow1"), Source("slow0", sku: "ProvisionedManaged"),
            Source("peer1"), Source("peer2"), Source("peer3"));
        foreach (var name in new[] { "peer1", "peer2", "peer3" })
            fixture.Sample(name, 1);
        fixture.Sample("slow1", 4);
        fixture.Sample("slow0", 4);
        fixture.Random.Draw = 0.5;
        Assert.All(fixture.Health.GetSnapshot().Where(state => state.DeploymentId.Contains("-slow")),
            state => Assert.Equal(DeploymentHealth.Degraded, state.State));
        var result = fixture.Select(tried: [fixture.Id("peer1"), fixture.Id("peer2"), fixture.Id("peer3")]);
        using var attempt = result.Attempt;
        Assert.Equal("slow0", result.Deployment?.DeploymentName);
    }

    [Fact]
    public void SkipsThrottledOpenAndMisconfiguredDeployments()
    {
        var fixture = new Fixture(Source("throttled"), Source("open"), Source("misconfigured"), Source("healthy"));
        fixture.Finish("throttled", HealthOutcome.Throttled, "20");
        fixture.Trip("open");
        fixture.Finish("misconfigured", HealthOutcome.Misconfigured);
        var result = fixture.Select();
        using var attempt = result.Attempt;
        Assert.Equal("healthy", result.Deployment?.DeploymentName);
    }

    [Fact]
    public void ReturnsTheShortestThrottleOfEligibleUntriedDeployments()
    {
        var fixture = new Fixture(Source("long"), Source("short"), Source("tried"),
            Source("foreign") with { Region = "eastus" });
        fixture.Finish("long", HealthOutcome.Throttled, "30");
        fixture.Finish("short", HealthOutcome.Throttled, "10");
        fixture.Finish("tried", HealthOutcome.Throttled, "1");
        fixture.Finish("foreign", HealthOutcome.Throttled, "1");
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        var result = fixture.Select(tried: [fixture.Id("tried")]);
        Assert.Null(result.Deployment);
        Assert.Null(result.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(8), result.RetryAfter);
    }

    [Fact]
    public void ReturnsUnavailableWhenNoEligibleThrottleExistsIncludingAnOpenAccount()
    {
        var fixture = new Fixture(Source("only"));
        using var concurrent = fixture.Health.TryAcquire(fixture.Id("only"))!;
        fixture.Finish("only", HealthOutcome.Throttled, "60");
        concurrent.Complete(HealthOutcome.AccountFailure);
        var result = fixture.Select();
        Assert.Null(result.Deployment);
        Assert.Null(result.Attempt);
        Assert.Null(result.RetryAfter);
        var unknown = fixture.Select(model: new("unknown", "version"));
        Assert.Null(unknown.Deployment);
        Assert.Null(unknown.RetryAfter);
    }

    [Fact]
    public void HalfOpenProbeCanRecoverAnOpenDeploymentAndAccount()
    {
        var fixture = new Fixture(Source("only"));
        fixture.Trip("only");
        Assert.Null(fixture.Select().Deployment);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        var result = fixture.Select();
        Assert.Equal("only", result.Deployment?.DeploymentName);
        Assert.NotNull(result.Attempt);
        Assert.Null(fixture.Select().Deployment);
        result.Attempt.Complete(HealthOutcome.Success);
        Assert.Equal(DeploymentHealth.Healthy, Assert.Single(fixture.Health.GetSnapshot()).State);
    }

    [Fact]
    public void FailedAcquisitionTriesAnotherDeploymentAndTerminatesWhenAllClaimsFail()
    {
        var fixture = new Fixture(Source("a"), Source("b"));
        var racing = new SnapshotHealth(fixture.Health.GetSnapshot(), fixture.Health) { RejectCount = 1 };
        var selector = new DeploymentSelector(racing, fixture.Random);
        var result = selector.Select(fixture.Table, RoutingPool.For(Model), "eu", new HashSet<string>());
        using var attempt = result.Attempt;
        Assert.Equal("b", result.Deployment?.DeploymentName);
        Assert.Equal(2, racing.Acquisitions);
        racing.RejectCount = int.MaxValue;
        racing.Acquisitions = 0;
        var unavailable = selector.Select(fixture.Table, RoutingPool.For(Model), "eu", new HashSet<string>());
        Assert.Null(unavailable.Deployment);
        Assert.Equal(2, racing.Acquisitions);
    }

    private static DiscoveredDeployment Source(string name, string sku = "Standard", decimal weight = 100) =>
        DiscoveryRefreshTests.Deployment(name, weight) with
        { AccountId = DiscoveryRefreshTests.Account.Id + "-" + name, AccountName = name, Sku = sku };

    private sealed class Fixture
    {
        public TestClock Clock { get; } = new();
        public HealthState Health { get; }
        public RoutingOverrides Overrides { get; } = new();
        public FixedRandom Random { get; } = new();
        public RoutingTable Table { get; private set; } = null!;
        private readonly DiscoveredDeployment[] deployments;

        public Fixture(params DiscoveredDeployment[] deployments)
        {
            this.deployments = deployments;
            Health = new(Clock);
            Refresh();
        }

        public void Refresh()
        {
            Table = new RoutingTableBuilder().Build(deployments,
                new(new Dictionary<string, string> { ["westeurope"] = "Europe", ["eastus"] = "United States" }), Overrides);
            Health.Reconcile(Table, Overrides.ModelHealth);
        }

        public string Id(string name) => Table.Deployments.Single(deployment => deployment.DeploymentName == name).Id;
        public SelectionResult Select(string zone = "eu", string[]? tried = null, ModelKey? model = null) =>
            new DeploymentSelector(Health, Random).Select(Table, RoutingPool.For(model ?? Model), zone,
                (tried ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase));
        public void Finish(string name, HealthOutcome outcome, string? retryAfter = null) =>
            Health.TryAcquire(Id(name))!.Complete(outcome, retryAfter: retryAfter);
        public void Trip(string name)
        {
            for (var i = 0; i < 3; i++)
                Finish(name, HealthOutcome.Failure);
        }
        public void Sample(string name, double seconds)
        {
            for (var i = 0; i < 20; i++)
                Health.TryAcquire(Id(name))!.Complete(HealthOutcome.Success, ttfb: TimeSpan.FromSeconds(seconds));
        }
    }

    private sealed class FixedRandom : ISelectionRandom
    {
        public double Draw { get; set; }
        public double NextDouble() => Draw;
    }

    private sealed class SnapshotHealth(IReadOnlyList<DeploymentHealthSnapshot> snapshots, IHealthState inner) : IHealthState
    {
        public int RejectCount { get; set; }
        public int Acquisitions { get; set; }
        public void Reconcile(RoutingTable table, IReadOnlyDictionary<string, ModelHealthOverride> modelOverrides) =>
            throw new NotSupportedException();
        public IReadOnlyList<DeploymentHealthSnapshot> GetSnapshot() => snapshots;
        public HealthAttempt? TryAcquire(string deploymentId) =>
            ++Acquisitions <= RejectCount ? null : inner.TryAcquire(deploymentId);
    }
}
