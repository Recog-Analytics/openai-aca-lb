using openai_loadbalancer.Configuration;
using openai_loadbalancer.Health;
using openai_loadbalancer.Operations;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Tests;

public class HealthTransitionTests
{
    [Fact]
    public void RecordsThrottleAndExpiryOnceWithoutInitialHealthyEvents()
    {
        var fixture = new Fixture();
        Assert.Empty(fixture.Observer.Transitions);
        fixture.Complete("a", HealthOutcome.Throttled, "1");
        fixture.State.GetSnapshot();
        fixture.Refresh();
        var throttled = Assert.Single(fixture.Observer.Transitions);
        Assert.Equal(DeploymentHealth.Throttled, throttled.Current);
        Assert.Equal(TimeSpan.FromSeconds(1), throttled.RetryAfter);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        using var attempt = fixture.Acquire("a");
        fixture.State.GetSnapshot();
        Assert.Equal(2, fixture.Observer.Transitions.Count);
        Assert.Equal(DeploymentHealth.Healthy, fixture.Observer.Transitions[1].Current);
    }

    [Fact]
    public void RecordsAccountOpenAndRecoveryForEverySibling()
    {
        var fixture = new Fixture();
        fixture.Complete("a", HealthOutcome.AccountFailure);
        Assert.Equal(3, fixture.Observer.Transitions.Count);
        Assert.All(fixture.Observer.Transitions, transition => Assert.Equal(DeploymentHealth.Open, transition.Current));
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        fixture.Complete("b", HealthOutcome.Success);
        Assert.Equal(6, fixture.Observer.Transitions.Count);
        Assert.All(fixture.Observer.Transitions.Skip(3), transition => Assert.Equal(DeploymentHealth.Healthy, transition.Current));
    }

    [Fact]
    public void RecordsMisconfigurationRecoveryAndManualDisableChanges()
    {
        var fixture = new Fixture();
        fixture.Complete("a", HealthOutcome.Misconfigured);
        Assert.True(Assert.Single(fixture.Observer.Transitions).Misconfigured);
        fixture.Refresh();
        Assert.Equal(DeploymentHealth.Healthy, fixture.Observer.Transitions[1].Current);
        Assert.False(fixture.Observer.Transitions[1].Misconfigured);
        fixture.Overrides.Deployments.Add(new() { Account = "oai", Deployment = "a", Disabled = true });
        fixture.Refresh();
        Assert.Equal(DeploymentHealth.Disabled, fixture.Observer.Transitions[2].Current);
        Assert.False(fixture.Observer.Transitions[2].Misconfigured);
        fixture.Refresh();
        Assert.Equal(3, fixture.Observer.Transitions.Count);
        fixture.Overrides.Deployments.Clear();
        fixture.Refresh();
        Assert.Equal(DeploymentHealth.Healthy, fixture.Observer.Transitions[3].Current);
    }

    [Fact]
    public void InitialManualDisableProducesATransition()
    {
        var fixture = new Fixture(initialDisabled: true);
        var transition = Assert.Single(fixture.Observer.Transitions);
        Assert.Equal(DeploymentHealth.Healthy, transition.Previous);
        Assert.Equal(DeploymentHealth.Disabled, transition.Current);
    }

    [Fact]
    public void MisconfigurationRefreshRecordsRecoveryEvenIfManualDrainKeepsItDisabled()
    {
        var fixture = new Fixture();
        fixture.Complete("a", HealthOutcome.Misconfigured);
        fixture.Overrides.Deployments.Add(new() { Account = "oai", Deployment = "a", Disabled = true });
        fixture.Refresh();
        Assert.Equal(2, fixture.Observer.Transitions.Count);
        var transition = fixture.Observer.Transitions[1];
        Assert.Equal(DeploymentHealth.Disabled, transition.Previous);
        Assert.Equal(DeploymentHealth.Disabled, transition.Current);
        Assert.False(transition.Misconfigured);
        fixture.Refresh();
        Assert.Equal(2, fixture.Observer.Transitions.Count);
    }

    [Fact]
    public void RecordsLatencyRecoveryAfterTheHysteresisPeriod()
    {
        var fixture = new Fixture();
        fixture.Sample("b", 1);
        fixture.Sample("a", 3);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        fixture.Sample("b", 1);
        fixture.Sample("a", 1);
        for (var i = 0; i < 10; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            fixture.Sample("b", 1);
            fixture.Sample("a", 1);
        }
        Assert.Equal(2, fixture.Observer.Transitions.Count);
        Assert.Equal(DeploymentHealth.Healthy, fixture.Observer.Transitions[1].Current);
    }

    [Fact]
    public void RecordsDegradationAndIntermediateRecoveryFromOpen()
    {
        var fixture = new Fixture();
        fixture.Sample("b", 1);
        fixture.Sample("a", 3);
        var degraded = Assert.Single(fixture.Observer.Transitions);
        Assert.Equal(DeploymentHealth.Degraded, degraded.Current);
        for (var i = 0; i < 3; i++)
            fixture.Complete("a", HealthOutcome.Failure);
        Assert.Equal(DeploymentHealth.Open, fixture.Observer.Transitions[1].Current);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        fixture.Complete("a", HealthOutcome.Success);
        var recovered = fixture.Observer.Transitions[2];
        Assert.Equal(DeploymentHealth.Open, recovered.Previous);
        Assert.Equal(DeploymentHealth.Degraded, recovered.Current);
    }

    [Fact]
    public void ObserverFailuresCannotPreventHealthUpdates()
    {
        var fixture = new Fixture();
        fixture.Observer.Throw = true;
        fixture.Complete("a", HealthOutcome.AccountFailure);
        Assert.All(fixture.State.GetSnapshot(), snapshot => Assert.Equal(DeploymentHealth.Open, snapshot.State));
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        fixture.Complete("b", HealthOutcome.Success);
        Assert.All(fixture.State.GetSnapshot(), snapshot => Assert.Equal(DeploymentHealth.Healthy, snapshot.State));
    }

    private sealed class Fixture
    {
        public TestClock Clock { get; } = new();
        public Observer Observer { get; } = new();
        public HealthState State { get; }
        public RoutingOverrides Overrides { get; } = new();
        private readonly List<DiscoveredDeployment> deployments = [.. new[] { "a", "b", "c" }
            .Select(name => DiscoveryRefreshTests.Deployment(name))];

        public Fixture(bool initialDisabled = false)
        {
            State = new(Clock, Observer);
            if (initialDisabled)
                Overrides.Deployments.Add(new() { Account = "oai", Deployment = "a", Disabled = true });
            Refresh();
        }

        public void Refresh() => State.Reconcile(new RoutingTableBuilder().Build(deployments,
            new(new Dictionary<string, string> { ["westeurope"] = "Europe" }), Overrides), Overrides.ModelHealth);

        public HealthAttempt? Acquire(string name) => State.TryAcquire(DiscoveryRefreshTests.Account.Id + "/deployments/" + name);

        public void Complete(string name, HealthOutcome outcome, string? retryAfter = null)
        {
            using var attempt = Acquire(name);
            Assert.NotNull(attempt);
            attempt.Complete(outcome, retryAfter: retryAfter);
        }

        public void Sample(string name, int seconds)
        {
            for (var i = 0; i < 20; i++)
            {
                using var attempt = Acquire(name);
                Assert.NotNull(attempt);
                attempt.RecordTtfb(TimeSpan.FromSeconds(seconds));
            }
        }
    }

    private sealed class Observer : IHealthObserver
    {
        public List<HealthTransition> Transitions { get; } = [];
        public bool Throw { get; set; }

        public void OnTransition(HealthTransition transition)
        {
            if (Throw)
                throw new InvalidOperationException("observer unavailable");
            Transitions.Add(transition);
        }
    }
}
