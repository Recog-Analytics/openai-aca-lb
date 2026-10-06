using openai_loadbalancer.Dashboard;
using openai_loadbalancer.Dashboard.Server;
using openai_loadbalancer.Operations;

namespace openai_loadbalancer.Tests;

public class DashboardStoreTests
{
    [Theory]
    [InlineData("Healthy", "Degraded", "Degraded")]
    [InlineData("Degraded", "Throttled", "Throttled")]
    [InlineData("Throttled", "Open", "Open")]
    [InlineData("Open", "Disabled", "Disabled")]
    [InlineData("Disabled", "Healthy", "Disabled")]
    public void MergeUsesWorstStateAndPreservesEveryReplica(string first, string second, string expected)
    {
        var clock = new TestClock();
        var store = new DashboardStore(clock);
        store.Ingest(Batch(clock, "one", first));
        store.Ingest(Batch(clock, "two", second) with
        {
            State = new(clock.GetUtcNow(), [Deployment(second) with { Id = "DEPLOYMENT", P95TtfbMs = 2500, AccountOpen = true }])
        });
        var deployment = Assert.Single(store.Snapshot().Deployments);
        Assert.Equal(expected, deployment.Deployment.State);
        Assert.Equal(2500, deployment.Deployment.P95TtfbMs);
        Assert.True(deployment.Deployment.AccountOpen);
        Assert.Equal([first, second], deployment.Replicas.Select(item => item.State));
    }

    [Fact]
    public void MergeKeepsTheWorstReplicaCooldownAndProbeFields()
    {
        var clock = new TestClock();
        var store = new DashboardStore(clock);
        var now = clock.GetUtcNow();
        store.Ingest(Batch(clock, "one", "Throttled") with
        {
            State = new(now, [Deployment("Throttled") with { ThrottledUntil = now.AddSeconds(10) }])
        });
        store.Ingest(Batch(clock, "two", "Open") with
        {
            State = new(now, [Deployment("Open") with { OpenUntil = now.AddSeconds(-1), HalfOpen = true }])
        });
        var merged = Assert.Single(store.Snapshot().Deployments).Deployment;
        Assert.Equal(now.AddSeconds(-1), merged.OpenUntil);
        Assert.True(merged.HalfOpen);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(merged,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        Assert.True(json.RootElement.GetProperty("halfOpen").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement.GetProperty("throttledUntil").ValueKind);
    }

    [Fact]
    public void LatestReplicaSnapshotReplacesItsDeploymentsAndRejectsOlderState()
    {
        var clock = new TestClock();
        var store = new DashboardStore(clock);
        var old = Batch(clock, "one", "Healthy");
        store.Ingest(old);
        clock.Advance(TimeSpan.FromSeconds(1));
        store.Ingest(Batch(clock, "one", "Open"));
        store.Ingest(old);
        Assert.Equal("Open", Assert.Single(store.Snapshot().Deployments).Deployment.State);
        store.Ingest(Batch(clock, "one", "Healthy") with { State = new(clock.GetUtcNow(), []) });
        Assert.Empty(store.Snapshot().Deployments);
    }

    [Fact]
    public void ReplicaExpiresAtThirtySecondsAndMergeRecovers()
    {
        var clock = new TestClock();
        var store = new DashboardStore(clock);
        store.Ingest(Batch(clock, "old", "Open"));
        clock.Advance(TimeSpan.FromSeconds(29));
        store.Ingest(Batch(clock, "fresh", "Healthy"));
        Assert.Equal(2, store.Snapshot().Replicas.Count);
        clock.Advance(TimeSpan.FromSeconds(1));
        var frame = store.Snapshot();
        Assert.Equal("fresh", Assert.Single(frame.Replicas));
        Assert.Equal("Healthy", Assert.Single(frame.Deployments).Deployment.State);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(store.Tick().Deployments);
    }

    [Fact]
    public void HistoryUsesReceiptTimeSoLongStreamsRemainForTwoMinutesAfterCompletion()
    {
        var clock = new TestClock();
        var store = new DashboardStore(clock);
        store.Ingest(Batch(clock, "one", "Healthy") with { Requests = [Request("stream") with { StartedAt = DateTimeOffset.UnixEpoch, Streaming = true }] });
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Single(store.Snapshot().Requests);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(store.Snapshot().Requests);
    }

    [Fact]
    public void BrowserSamplingIsGlobalAndDoesNotSampleCountsOrRetainedHistory()
    {
        var clock = new TestClock();
        var store = new DashboardStore(clock);
        for (var index = 0; index < 4; index++)
            store.Ingest(Batch(clock, index.ToString(), "Healthy") with
            {
                Requests = Enumerable.Range(index * 200, 200).Select(value => Request(value.ToString())).ToArray(),
                Counts = [new("deployment", "Success", 1000), new("deployment", "Throttled", 100)]
            });
        clock.Advance(TimeSpan.FromSeconds(1));
        var frame = store.Tick();
        Assert.Equal(100, frame.Requests.Count);
        Assert.Equal(100, frame.Requests.Select(item => item.Id).Distinct().Count());
        var rate = Assert.Single(frame.Counts);
        Assert.Equal(4400, rate.RequestsPerSecond);
        Assert.Equal(4000, rate.Outcomes.Single(item => item.Outcome == "Success").Count);
        Assert.Equal(400, rate.Outcomes.Single(item => item.Outcome == "Throttled").Count);
        Assert.Equal(800, store.Snapshot().Requests.Count);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(store.Tick().Requests);
        Assert.Empty(store.Snapshot().Counts);
        // The snapshot keeps what a browser keeps from deltas: 100 samples a second over two minutes, newest first in.
        for (var batch = 0; batch < 60; batch++)
            store.Ingest(Batch(clock, "0", "Healthy") with { Requests = Enumerable.Range(1000 + batch * 200, 200).Select(value => Request(value.ToString())).ToArray() });
        var snapshot = store.Snapshot().Requests;
        Assert.Equal(DashboardHistory.MaxRecentRequests, snapshot.Count);
        Assert.Equal(12_000, DashboardHistory.MaxRecentRequests);
        Assert.Equal("12999", snapshot[^1].Id);
        Assert.DoesNotContain(snapshot, item => item.Id == "0");
    }

    [Fact]
    public void RoutesAddUpAcrossReplicasPerTickAndTheSnapshotCarriesTwoMinutesOfTicks()
    {
        var clock = new TestClock();
        var store = new DashboardStore(clock);
        DashboardRoute Route(string caller, long count) => new(caller, "gpt-4o@1", "eu", 200, [new("deployment", "Success")], count);
        store.Ingest(Batch(clock, "one", "Healthy") with { Routes = [Route("dev", 30), Route("batch", 5)] });
        store.Ingest(Batch(clock, "two", "Healthy") with { Routes = [Route("dev", 12)] });
        clock.Advance(TimeSpan.FromSeconds(1));
        var first = store.Tick();
        Assert.Equal(42, first.Routes.Single(item => item.Caller == "dev").Count);
        Assert.Equal(5, first.Routes.Single(item => item.Caller == "batch").Count);
        Assert.Empty(first.RouteHistory);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(store.Tick().Routes);
        // An older publisher sends no routes at all.
        store.Ingest(Batch(clock, "old", "Healthy"));
        var snapshot = store.Snapshot();
        Assert.Empty(snapshot.Routes);
        Assert.Equal(2, snapshot.RouteHistory.Count);
        Assert.Equal(first.At, snapshot.RouteHistory[0].At);
        Assert.Equal(47, snapshot.RouteHistory[0].Routes.Sum(item => item.Count));
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(snapshot,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        var hop = json.RootElement.GetProperty("routeHistory")[0].GetProperty("routes")[0].GetProperty("hops")[0];
        Assert.Equal("deployment", hop.GetProperty("deploymentId").GetString());
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Single(store.Snapshot().RouteHistory);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(store.Snapshot().RouteHistory);
    }

    [Fact]
    public void MultipleSubscribersReceiveTheSameDeltaAndSlowReadersStayBounded()
    {
        var clock = new TestClock();
        var store = new DashboardStore(clock);
        var one = store.Subscribe();
        var two = store.Subscribe();
        Assert.Empty(one.Snapshot.Replicas);
        for (var index = 0; index < 10; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            store.Tick();
        }
        Assert.Equal(2, one.Channel.Reader.Count);
        Assert.True(one.Channel.Reader.TryRead(out var first));
        Assert.True(two.Channel.Reader.TryRead(out var second));
        Assert.Same(first, second);
        store.Unsubscribe(one.Channel);
        store.Unsubscribe(two.Channel);
    }

    internal static DashboardDeployment Deployment(string state) =>
        new("deployment", "account", "chat", "swedencentral", "gpt-4o@1", 1, "eu", 100, state, 50, false);

    internal static DashboardBatch Batch(TimeProvider clock, string replica, string state) =>
        new(replica, clock.GetUtcNow(), new(clock.GetUtcNow(), [Deployment(state)]), [], []);

    internal static RequestRecord Request(string id) => new(id, DateTimeOffset.UtcNow, "dev", "gpt-4o", "gpt-4o@1",
        "eu", false, 200, 50, [new("chat", "account", "swedencentral", 1, 200, 20, "Success", null, "deployment")], "success");
}
