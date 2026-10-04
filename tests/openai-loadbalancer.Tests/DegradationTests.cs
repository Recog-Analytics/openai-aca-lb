using openai_loadbalancer.Configuration;
using openai_loadbalancer.Health;

namespace openai_loadbalancer.Tests;

public class DegradationTests
{
    [Fact]
    public void RequiresTwentySamplesAndBothRelativeAndAbsoluteThresholds()
    {
        var fixture = new HealthStateTests.Fixture();
        fixture.Sample("peer1", 1);
        fixture.Sample("peer2", 1);
        fixture.Sample("a", 3, 19);
        Assert.Null(fixture.Snapshot("a").P95);
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
        fixture.Sample("a", 3, 1);
        Assert.Equal(DeploymentHealth.Degraded, fixture.Snapshot("a").State);
        // A degraded deployment remains eligible for the phase 4 selector's probe traffic.
        using var attempt = fixture.Acquire("a");
        Assert.NotNull(attempt);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(0.1, 1.9)]
    [InlineData(2, 3.9)]
    public void StrictThresholdAndDefaultFloorKeepFastDeploymentsHealthy(double peer, double target)
    {
        var fixture = new HealthStateTests.Fixture();
        fixture.Sample("peer1", peer);
        fixture.Sample("a", target);
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
    }

    [Fact]
    public void MedianUsesOtherDeploymentsOfTheExactModelKey()
    {
        var fixture = new HealthStateTests.Fixture("a", "peer1", "peer2", "different");
        fixture.Deployments[3] = fixture.Deployments[3] with { Model = new("gpt-4o", "other-version") };
        fixture.Refresh();
        fixture.Sample("peer1", 1);
        fixture.Sample("peer2", 3);
        fixture.Sample("different", 0.01);
        fixture.Sample("a", 4);
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
        fixture.Sample("a", 4.1);
        Assert.Equal(DeploymentHealth.Degraded, fixture.Snapshot("a").State);
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("different").State);
    }

    [Fact]
    public void UsesNearestRankP95RatherThanMaximumOrTotalDuration()
    {
        var fixture = new HealthStateTests.Fixture();
        fixture.Sample("peer1", 1);
        fixture.Sample("a", 1, 19);
        fixture.Sample("a", 100, 1);
        Assert.Equal(TimeSpan.FromSeconds(1), fixture.Snapshot("a").P95);
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
    }

    [Fact]
    public void SamplesExpireAtFiveMinutesAndModelChangeClearsLatency()
    {
        var fixture = new HealthStateTests.Fixture();
        fixture.Sample("a", 3);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(fixture.Snapshot("a").P95);
        fixture.Sample("a", 3);
        fixture.Deployments[0] = fixture.Deployments[0] with { Model = new("gpt-4o", "new-version") };
        fixture.Refresh();
        Assert.Null(fixture.Snapshot("a").P95);
    }

    [Fact]
    public void RecoveryRequiresTenMinutesContinuouslyBelowHysteresisThreshold()
    {
        var fixture = new HealthStateTests.Fixture();
        fixture.Sample("peer1", 1);
        fixture.Sample("a", 3);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        fixture.Sample("peer1", 1);
        fixture.Sample("a", 1.4);
        for (var i = 0; i < 9; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            fixture.Sample("peer1", 1);
            fixture.Sample("a", 1.4);
            Assert.Equal(DeploymentHealth.Degraded, fixture.Snapshot("a").State);
        }
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Sample("peer1", 1);
        fixture.Sample("a", 1.4);
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
    }

    [Fact]
    public void MissingSamplesAndThresholdCrossingRestartRecovery()
    {
        var fixture = new HealthStateTests.Fixture();
        fixture.Sample("peer1", 1);
        fixture.Sample("a", 3);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        fixture.Sample("peer1", 1);
        fixture.Sample("a", 1.4);
        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        fixture.Sample("peer1", 1);
        fixture.Sample("a", 1.5);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(DeploymentHealth.Degraded, fixture.Snapshot("a").State);
        fixture.Sample("peer1", 1);
        fixture.Sample("a", 1.4);
        for (var i = 0; i < 9; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            fixture.Sample("peer1", 1);
            fixture.Sample("a", 1.4);
        }
        Assert.Equal(DeploymentHealth.Degraded, fixture.Snapshot("a").State);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Sample("peer1", 1);
        fixture.Sample("a", 1.4);
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
    }

    [Fact]
    public void NoPeerNeedsOptionalAbsoluteThresholdAndSupportsRecovery()
    {
        var fixture = new HealthStateTests.Fixture("a");
        fixture.Sample("a", 100);
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
        fixture.Overrides.ModelHealth["gpt-4o@2024-11-20"] = new() { DegradedThresholdSeconds = 4 };
        fixture.Refresh();
        Assert.Equal(DeploymentHealth.Degraded, fixture.Snapshot("a").State);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        fixture.Sample("a", 2.9);
        for (var i = 0; i < 10; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            fixture.Sample("a", 2.9);
        }
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
    }

    [Fact]
    public void ModelSpecificFloorReloadsAndRetainsSamples()
    {
        var fixture = new HealthStateTests.Fixture();
        fixture.Overrides.ModelHealth["gpt-4o@2024-11-20"] = new() { DegradedFloorSeconds = 5 };
        fixture.Refresh();
        fixture.Sample("peer1", 1);
        fixture.Sample("a", 4);
        Assert.Equal(DeploymentHealth.Healthy, fixture.Snapshot("a").State);
        fixture.Overrides.ModelHealth.Clear();
        fixture.Refresh();
        Assert.Equal(DeploymentHealth.Degraded, fixture.Snapshot("a").State);
    }

    [Fact]
    public void ParsesModelHealthOverrides()
    {
        var config = new YamlConfigurationParser().ParseOverrides("""
            modelHealth:
              gpt-4o@2024-11-20:
                degradedFloorSeconds: 3
                degradedThresholdSeconds: 7
            """);
        var item = Assert.Single(config.ModelHealth).Value;
        Assert.Equal(3, item.DegradedFloorSeconds);
        Assert.Equal(7, item.DegradedThresholdSeconds);
    }

    [Theory]
    [InlineData("gpt-4o", "degradedFloorSeconds: 2")]
    [InlineData("gpt-4o@v", "degradedFloorSeconds: 0")]
    [InlineData("gpt-4o@v", "degradedFloorSeconds: -1")]
    [InlineData("gpt-4o@v", "degradedFloorSeconds: .nan")]
    [InlineData("gpt-4o@v", "degradedThresholdSeconds: .inf")]
    [InlineData("gpt-4o@v", "degradedThresholdSeconds: -3")]
    public void RejectsInvalidHealthOverrides(string model, string setting)
    {
        Assert.Throws<ArgumentException>(() => new YamlConfigurationParser().ParseOverrides($"modelHealth:\n  {model}: {{ {setting} }}\n"));
    }
}
