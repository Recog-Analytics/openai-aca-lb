using openai_loadbalancer.Configuration;
using openai_loadbalancer.Health;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Tests;

public class HealthStateTests
{
    [Theory]
    [InlineData(null, null, 10)]
    [InlineData("5000", "90", 5)]
    [InlineData("bad", "12", 12)]
    [InlineData("NaN", "8", 8)]
    [InlineData("Infinity", null, 10)]
    [InlineData("0", null, 1)]
    [InlineData("-5000", "15", 1)]
    [InlineData("99999999999", null, 120)]
    [InlineData(null, "1000", 120)]
    [InlineData(null, "invalid", 10)]
    [InlineData(null, "0", 1)]
    public void ParsesAndClampsRetryAfter(string? milliseconds, string? seconds, int expected)
    {
        Assert.Equal(TimeSpan.FromSeconds(expected), RetryAfterParser.Parse(milliseconds, seconds, new TestClock().GetUtcNow()));
    }

    [Theory]
    [InlineData(-60, 1)]
    [InlineData(15, 15)]
    [InlineData(500, 120)]
    public void ParsesHttpDateAgainstTheInjectedClock(int offset, int expected)
    {
        var now = new TestClock().GetUtcNow();
        Assert.Equal(TimeSpan.FromSeconds(expected), RetryAfterParser.Parse(null, now.AddSeconds(offset).ToString("r"), now));
    }

    [Fact]
    public void ThrottleExpiresAndNeverTripsTheCircuit()
    {
        var fixture = new Fixture();
        for (var i = 0; i < 10; i++)
        {
            fixture.Finish("a", HealthOutcome.Throttled, retryAfter: "1");
            Assert.Equal(DeploymentHealth.Throttled, fixture.Snapshot("a").State);
            Assert.Equal(TimeSpan.FromSeconds(1), fixture.Snapshot("a").RetryAfter);
            Assert.Null(fixture.Acquire("a"));
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
        }
    }

    [Fact]
    public void ConcurrentThrottleResponsesKeepTheLongestCooldown()
    {
        var fixture = new Fixture();
        using var first = fixture.Acquire("a")!;
        using var second = fixture.Acquire("a")!;
        first.Complete(HealthOutcome.Throttled, retryAfter: "100");
        second.Complete(HealthOutcome.Throttled, retryAfter: "1");
        Assert.Equal(TimeSpan.FromSeconds(100), fixture.Snapshot("a").RetryAfter);
    }

    [Fact]
    public void ThreeConsecutiveFailuresTripAndOneProbeSuccessResetsBackoff()
    {
        var fixture = new Fixture();
        fixture.Trip("a");
        Assert.Equal(DeploymentHealth.Open, fixture.Snapshot("a").State);
        Assert.Null(fixture.Acquire("a"));
        fixture.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Null(fixture.Acquire("a"));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        using var probe = fixture.Acquire("a");
        Assert.NotNull(probe);
        Assert.Null(fixture.Acquire("a"));
        probe.Complete(HealthOutcome.Success);
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
        fixture.Trip("a");
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.NotNull(fixture.Acquire("a"));
    }

    [Fact]
    public void SnapshotReportsOpenDeadlineAndHalfOpenProbeWindow()
    {
        var fixture = new Fixture();
        Assert.Null(fixture.Snapshot("a").OpenUntil);
        fixture.Trip("a");
        var openedAt = fixture.Clock.GetUtcNow();
        Assert.Equal(openedAt.AddSeconds(30), fixture.Snapshot("a").OpenUntil);
        Assert.False(fixture.Snapshot("a").HalfOpen);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(fixture.Snapshot("a").HalfOpen);
        fixture.Finish("a", HealthOutcome.Success);
        Assert.Null(fixture.Snapshot("a").OpenUntil);
        Assert.False(fixture.Snapshot("a").HalfOpen);
    }

    [Fact]
    public void FailedProbesDoubleOpenTimeUpToFiveMinutes()
    {
        var fixture = new Fixture();
        fixture.Trip("a");
        foreach (var delay in new[] { 30, 60, 120, 240, 300, 300 })
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(delay - 1));
            Assert.Null(fixture.Acquire("a"));
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            fixture.Finish("a", HealthOutcome.Failure);
        }
    }

    [Fact]
    public void FiveFailuresRequireMoreThanHalfAndSuccessBreaksConsecutiveFailures()
    {
        var fixture = new Fixture();
        for (var i = 0; i < 5; i++)
        {
            fixture.Finish("a", HealthOutcome.Success);
            fixture.Finish("a", HealthOutcome.Failure);
        }
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
        fixture.Finish("a", HealthOutcome.Failure);
        Assert.Equal(DeploymentHealth.Open, fixture.Snapshot("a").State);
    }

    [Fact]
    public void BreakerSamplesExpireAtThirtySeconds()
    {
        var fixture = new Fixture();
        fixture.Finish("a", HealthOutcome.Failure);
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        fixture.Finish("a", HealthOutcome.Failure);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.Finish("a", HealthOutcome.Failure);
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
        fixture.Finish("a", HealthOutcome.Failure);
        Assert.Equal(DeploymentHealth.Open, fixture.Snapshot("a").State);
    }

    [Fact]
    public void IgnoredCompletionAndDisposalReleaseProbesWithoutChangingHealth()
    {
        var fixture = new Fixture();
        fixture.Trip("a");
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        fixture.Acquire("a")!.Dispose();
        fixture.Finish("a", HealthOutcome.Ignored);
        fixture.Finish("a", HealthOutcome.Success);
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
    }

    [Fact]
    public void LateSuccessDoesNotCloseAnOpenCircuitAndCompletionIsIdempotent()
    {
        var fixture = new Fixture();
        using var late = fixture.Acquire("a")!;
        fixture.Trip("a");
        late.Complete(HealthOutcome.Success);
        late.Complete(HealthOutcome.Success);
        Assert.Equal(DeploymentHealth.Open, fixture.Snapshot("a").State);
    }

    [Fact]
    public void ConnectionFailureOpensOnlyTheWholeAccountAndOtherAccountsStayAvailable()
    {
        var fixture = new Fixture("a", "b", "c");
        fixture.AddAccount("other");
        fixture.Finish("a", HealthOutcome.AccountFailure);
        Assert.All(new[] { "a", "b", "c" }, name =>
        {
            Assert.True(fixture.Snapshot(name).AccountOpen);
            Assert.Null(fixture.Acquire(name));
        });
        fixture.Finish("other", HealthOutcome.Success);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        using var probe = fixture.Acquire("b")!;
        Assert.Null(fixture.Acquire("c"));
        probe.Complete(HealthOutcome.Success);
        Assert.All(fixture.State.GetSnapshot(), item => Assert.False(item.AccountOpen));
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
    }

    [Fact]
    public void AccountProbeFailuresBackOffAndSuccessAllowsIndependentDeploymentRecovery()
    {
        var fixture = new Fixture("a", "b", "c");
        fixture.Trip("a");
        Assert.False(fixture.Snapshot("c").AccountOpen);
        fixture.Trip("b");
        Assert.True(fixture.Snapshot("c").AccountOpen);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        fixture.Finish("c", HealthOutcome.AccountFailure);
        fixture.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(fixture.Acquire("a"));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Finish("c", HealthOutcome.Success);
        Assert.Equal(DeploymentHealth.Open, fixture.Snapshot("a").State);
        fixture.Finish("a", HealthOutcome.Success);
        fixture.Finish("b", HealthOutcome.Success);
        Assert.All(fixture.State.GetSnapshot(), item => Assert.Equal(DeploymentHealth.Healthy, item.State));
    }

    [Fact]
    public void AccountDoesNotAggregateOrdinaryDeploymentFailures()
    {
        var fixture = new Fixture("a", "b", "c");
        foreach (var name in new[] { "a", "b", "c" })
            fixture.Finish(name, HealthOutcome.Failure);
        Assert.All(fixture.State.GetSnapshot(), item => Assert.False(item.AccountOpen));
    }

    [Fact]
    public void UnchangedRefreshPreservesRecoveredAccountWithOpenDeploymentCircuits()
    {
        var fixture = new Fixture("a", "b", "c");
        fixture.Trip("a");
        fixture.Trip("b");
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        fixture.Finish("c", HealthOutcome.Success);
        fixture.Refresh();
        Assert.False(fixture.Snapshot("a").AccountOpen);
        Assert.Equal(DeploymentHealth.Open, fixture.Snapshot("a").State);
        fixture.Finish("a", HealthOutcome.Success);
        fixture.Finish("b", HealthOutcome.Success);
        Assert.All(fixture.State.GetSnapshot(), item => Assert.Equal(DeploymentHealth.Healthy, item.State));
    }

    [Fact]
    public void AccountOpensOnlyWhenStrictlyMoreThanHalfItsDeploymentsOpen()
    {
        var fixture = new Fixture("a", "b");
        fixture.Trip("a");
        Assert.False(fixture.Snapshot("b").AccountOpen);
        fixture.Trip("b");
        Assert.True(fixture.Snapshot("b").AccountOpen);
    }

    [Fact]
    public void ParallelAcquisitionAllowsExactlyOneHalfOpenProbe()
    {
        var fixture = new Fixture();
        fixture.Trip("a");
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        var attempts = new HealthAttempt?[100];
        Parallel.For(0, attempts.Length, i => attempts[i] = fixture.Acquire("a"));
        Assert.Single(attempts, item => item != null)!.Dispose();
        fixture.Finish("a", HealthOutcome.Success);
    }

    [Fact]
    public void SuccessfulRefreshPreservesStableIdsAndDropsRemovedDeploymentsAndAccounts()
    {
        var fixture = new Fixture("a", "b", "c");
        fixture.Trip("a");
        fixture.Finish("b", HealthOutcome.Throttled, retryAfter: "100");
        fixture.Sample("c", 4);
        fixture.Deployments = fixture.Deployments.Select(item => item with
        {
            AccountId = item.AccountId.ToUpperInvariant(),
            DeploymentName = item.DeploymentName.ToUpperInvariant(),
            Capacity = 200
        }).ToList();
        fixture.Refresh();
        Assert.Equal(DeploymentHealth.Open, fixture.Snapshot("a").State);
        Assert.Equal(DeploymentHealth.Throttled, fixture.Snapshot("b").State);
        Assert.Equal(TimeSpan.FromSeconds(4), fixture.Snapshot("c").P95);
        fixture.Deployments.RemoveAll(item => item.DeploymentName != "C");
        fixture.Refresh();
        Assert.Single(fixture.State.GetSnapshot());
        fixture.Deployments.Clear();
        fixture.Refresh();
        Assert.Empty(fixture.State.GetSnapshot());
        fixture.Deployments.Add(DiscoveryRefreshTests.Deployment("a"));
        fixture.Refresh();
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
    }

    [Fact]
    public void RemovedInflightAttemptCannotMutateReappearingDeployment()
    {
        var fixture = new Fixture();
        using var old = fixture.Acquire("a")!;
        fixture.Deployments.Clear();
        fixture.Refresh();
        fixture.Deployments.Add(DiscoveryRefreshTests.Deployment("a"));
        fixture.Refresh();
        old.Complete(HealthOutcome.AccountFailure, ttfb: TimeSpan.FromSeconds(100));
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
        Assert.False(fixture.Snapshot("a").AccountOpen);
        Assert.Null(fixture.Snapshot("a").P95);
    }

    [Fact]
    public void RemovedDeploymentReleasesItsAccountProbeForSurvivingSiblings()
    {
        var fixture = new Fixture("a", "b");
        fixture.Finish("a", HealthOutcome.AccountFailure);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        using var probe = fixture.Acquire("a")!;
        fixture.Deployments.RemoveAt(0);
        fixture.Refresh();
        Assert.Null(fixture.Acquire("b"));
        probe.Complete(HealthOutcome.Success);
        Assert.True(fixture.Snapshot("b").AccountOpen);
        fixture.Finish("b", HealthOutcome.Success);
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("b").State);
    }

    [Fact]
    public void RemovalThatLeavesAMajorityOpenTripsTheAccount()
    {
        var fixture = new Fixture("a", "b", "c");
        fixture.Trip("a");
        fixture.Deployments.RemoveAll(item => item.DeploymentName != "a");
        fixture.Refresh();
        Assert.True(fixture.Snapshot("a").AccountOpen);
    }

    [Fact]
    public void ModelChangeIgnoresLatencyFromAnOldInflightAttempt()
    {
        var fixture = new Fixture();
        var attempts = Enumerable.Range(0, 20).Select(_ => fixture.Acquire("a")!).ToArray();
        fixture.Deployments[0] = fixture.Deployments[0] with { Model = new("gpt-4o", "new-version") };
        fixture.Refresh();
        foreach (var attempt in attempts)
            attempt.Complete(HealthOutcome.Success, ttfb: TimeSpan.FromSeconds(10));
        Assert.Null(fixture.Snapshot("a").P95);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeaderLatencyCannotUpdateAReplacementDeploymentOrModel(bool modelChange)
    {
        var fixture = new Fixture();
        var attempts = Enumerable.Range(0, 20).Select(_ => fixture.Acquire("a")!).ToArray();
        if (modelChange)
            fixture.Deployments[0] = fixture.Deployments[0] with { Model = new("gpt-4o", "new-version") };
        else
            fixture.Deployments.RemoveAt(0);
        fixture.Refresh();
        if (!modelChange)
        {
            fixture.Deployments.Add(DiscoveryRefreshTests.Deployment("a"));
            fixture.Refresh();
        }
        foreach (var attempt in attempts)
        {
            attempt.RecordTtfb(TimeSpan.FromSeconds(100));
            attempt.Dispose();
        }
        Assert.Null(fixture.Snapshot("a").P95);
        fixture.Sample("a", 1);
        Assert.Equal(TimeSpan.FromSeconds(1), fixture.Snapshot("a").P95);
    }

    [Fact]
    public void HeaderLatencyRecordsBeforeCompletionAndAtMostOncePerAttempt()
    {
        var fixture = new Fixture();
        var attempts = Enumerable.Range(0, 20).Select(_ => fixture.Acquire("a")!).ToArray();
        foreach (var attempt in attempts)
        {
            attempt.RecordTtfb(TimeSpan.FromSeconds(1));
            attempt.RecordTtfb(TimeSpan.FromSeconds(100));
        }
        Assert.Equal(TimeSpan.FromSeconds(1), fixture.Snapshot("a").P95);
        foreach (var attempt in attempts)
        {
            attempt.Complete(HealthOutcome.Success, ttfb: TimeSpan.FromSeconds(100));
            attempt.RecordTtfb(TimeSpan.FromSeconds(100));
        }
        Assert.Equal(TimeSpan.FromSeconds(1), fixture.Snapshot("a").P95);
    }

    [Fact]
    public void AcquiredAttemptRecordsHeaderLatencyAndRejectsNegativeSamples()
    {
        var fixture = new Fixture();
        for (var i = 0; i < 20; i++)
            fixture.Acquire("a")!.Complete(HealthOutcome.Success, ttfb: TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), fixture.Snapshot("a").P95);
        using var attempt = fixture.Acquire("a")!;
        Assert.Throws<ArgumentOutOfRangeException>(() => attempt.RecordTtfb(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => attempt.Complete(HealthOutcome.Success, ttfb: TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void MisconfigurationClearsOnRefreshButOverrideDisableDoesNot()
    {
        var fixture = new Fixture();
        fixture.Finish("a", HealthOutcome.Misconfigured);
        Assert.Equal(DeploymentHealth.Disabled, fixture.Snapshot("a").State);
        Assert.Null(fixture.Acquire("a"));
        fixture.Refresh();
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
        fixture.Overrides.Deployments.Add(new() { Account = "oai", Deployment = "a", Disabled = true });
        fixture.Refresh();
        Assert.Equal(DeploymentHealth.Disabled, fixture.Snapshot("a").State);
        Assert.Null(fixture.Acquire("a"));
        fixture.Overrides.Deployments.Clear();
        fixture.Refresh();
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
    }

    internal sealed class Fixture
    {
        public TestClock Clock { get; } = new();
        public HealthState State { get; }
        public List<DiscoveredDeployment> Deployments { get; set; }
        public RoutingOverrides Overrides { get; } = new();

        public Fixture(params string[] names)
        {
            State = new(Clock);
            Deployments = (names.Length == 0 ? ["a", "peer1", "peer2"] : names)
                .Select(name => DiscoveryRefreshTests.Deployment(name)).ToList();
            Refresh();
        }

        public void Refresh() => State.Reconcile(new RoutingTableBuilder().Build(Deployments,
            new(new Dictionary<string, string> { ["westeurope"] = "Europe" }), Overrides), Overrides.ModelHealth);

        public string Id(string name) => (Deployments.FirstOrDefault(item => string.Equals(item.DeploymentName, name,
            StringComparison.OrdinalIgnoreCase))?.AccountId ?? DiscoveryRefreshTests.Account.Id) + "/deployments/" + name;
        public DeploymentHealthSnapshot Snapshot(string name) => State.GetSnapshot().Single(item =>
            string.Equals(item.DeploymentId, Id(name), StringComparison.OrdinalIgnoreCase));
        public HealthAttempt? Acquire(string name) => State.TryAcquire(Id(name));
        public void Finish(string name, HealthOutcome outcome, string? retryAfter = null)
        {
            using var attempt = Acquire(name);
            Assert.NotNull(attempt);
            attempt.Complete(outcome, retryAfter: retryAfter);
        }
        public void Trip(string name)
        {
            for (var i = 0; i < 3; i++)
                Finish(name, HealthOutcome.Failure);
        }
        public void Sample(string name, double seconds, int count = 20)
        {
            for (var i = 0; i < count; i++)
            {
                using var attempt = Acquire(name)!;
                attempt.RecordTtfb(TimeSpan.FromSeconds(seconds));
            }
        }
        public void AddAccount(string name)
        {
            Deployments.Add(DiscoveryRefreshTests.Deployment(name) with { AccountId = DiscoveryRefreshTests.Account.Id + "-other" });
            Refresh();
        }
    }
}
