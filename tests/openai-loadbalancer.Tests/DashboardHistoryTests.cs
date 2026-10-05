using openai_loadbalancer.Dashboard;
using openai_loadbalancer.Dashboard.Server;
using openai_loadbalancer.Operations;
using static openai_loadbalancer.Tests.DashboardStoreTests;

namespace openai_loadbalancer.Tests;

public class DashboardHistoryTests
{
    private static DashboardRoute Route(string caller, int status = 200, int hops = 1, long count = 1) =>
        new(caller, "gpt-4o@1", "eu", status, Enumerable.Range(0, hops).Select(_ => new DashboardHop("deployment", "Success")).ToArray(), count);

    private static MergedDeployment Merged(string id, string state = "Healthy", double? p95 = 50) =>
        new(Deployment(state) with { Id = id, P95TtfbMs = p95 }, []);

    /// <summary>Runs one tick per second; <paramref name="state"/> gives the deployment state of each second.</summary>
    private static void Run(TestClock clock, DashboardStore store, int seconds, Func<int, string>? state = null, long count = 1)
    {
        for (var index = 0; index < seconds; index++)
        {
            store.Ingest(Batch(clock, "one", state?.Invoke(index) ?? "Healthy") with { Routes = [Route("dev", count: count)] });
            clock.Advance(TimeSpan.FromSeconds(1));
            store.Tick();
        }
    }

    [Fact]
    public void PerSecondDataLastsOneHourAndMinutesLastOneDay()
    {
        var clock = new TestClock();
        var store = new DashboardStore(clock);
        Run(clock, store, 3700);
        var now = clock.GetUtcNow();
        var snapshot = store.Snapshot();
        Assert.Equal(now.AddHours(-1), snapshot.Retention.SecondsFrom);
        var seconds = store.History(now.AddHours(-2), now, 30);
        Assert.Equal(now.AddHours(-1), seconds.From);
        Assert.Equal(3600, seconds.Buckets.Sum(bucket => bucket.Seconds));

        for (var minute = 0; minute < 1500; minute++)
        {
            store.Ingest(Batch(clock, "one", "Healthy") with { Routes = [Route("dev")] });
            clock.Advance(TimeSpan.FromMinutes(1));
            store.Tick();
        }
        now = clock.GetUtcNow();
        var summary = store.Snapshot().Summary;
        // Minute ends fall 20 s after a whole minute here, so the bucket ending just inside 24 h is kept.
        Assert.Equal(1440, summary.Count);
        Assert.True(now - summary[0].At < TimeSpan.FromHours(24));
        Assert.Equal(summary[0].At.AddMinutes(-1), store.Snapshot().Retention.MinutesFrom);
        Assert.Equal(now.AddSeconds(-40), summary[^1].At);
    }

    [Fact]
    public void EveryCapHoldsAndEvictsOldestFirst()
    {
        var history = new DashboardHistory();
        var at = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        var callers = Enumerable.Range(0, 1000).Select(index => Route("c" + index)).ToArray();
        var deployments = Enumerable.Range(0, 1000).Select(index => Merged("d" + index, "Degraded")).ToArray();
        MergedDeployment[] Changing(int tick) => deployments.Select(item => item with { Deployment = item.Deployment with { P95TtfbMs = tick } }).ToArray();

        // Per-second routes and state changes: 1000 of each per tick.
        for (var tick = 1; tick <= 250; tick++)
            history.Tick(at.AddSeconds(tick), callers, Changing(tick));
        Assert.True(history.SecondRouteCount <= DashboardHistory.MaxSecondRoutes);
        Assert.True(history.SecondStateCount <= DashboardHistory.MaxSecondStates);
        Assert.Equal(100, history.SecondCount);
        Assert.Equal(at.AddSeconds(150), history.Retention(at.AddSeconds(250)).SecondsFrom);

        // Ticks faster than one per second.
        var fast = new DashboardHistory();
        for (var tick = 1; tick <= 4000; tick++)
            fast.Tick(at.AddMilliseconds(500 * tick), [], []);
        Assert.Equal(DashboardHistory.MaxSeconds, fast.SecondCount);

        // Minute routes and states: one tick per minute closes a bucket of 1000 routes and 1000 states.
        var minutes = new DashboardHistory();
        for (var tick = 1; tick <= 450; tick++)
            minutes.Tick(at.AddMinutes(tick), callers, deployments);
        Assert.True(minutes.MinuteRouteCount <= DashboardHistory.MaxMinuteRoutes);
        Assert.True(minutes.MinuteStateCount <= DashboardHistory.MaxMinuteStates);
        Assert.Equal(100, minutes.MinuteCount);
        Assert.Equal(at.AddMinutes(449), minutes.Summary()[^1].At);
        Assert.Equal(at.AddMinutes(350), minutes.Summary()[0].At);

        // Shapes: new callers beyond the table count under "(other)" without losing requests.
        var shapes = new DashboardHistory();
        for (var tick = 1; tick <= 15; tick++)
            shapes.Tick(at.AddSeconds(tick), Enumerable.Range(0, 1000).Select(index => Route($"t{tick}-{index}")).ToArray(), []);
        Assert.Equal(DashboardHistory.MaxShapes, shapes.ShapeCount);
        var all = shapes.Query(at.AddSeconds(15), at, at.AddSeconds(15), 60).Buckets.SelectMany(bucket => bucket.Routes).ToArray();
        Assert.Equal(15_000, all.Sum(route => route.Count));
        Assert.Equal(5000, all.Single(route => route.Caller == "(other)").Count);

        var cut = new DashboardHistory();
        cut.Tick(at.AddSeconds(1), [Route(new string('c', 1000))], []);
        Assert.Equal(DashboardHistory.MaxText, cut.Query(at.AddSeconds(1), at, at.AddSeconds(1), 1).Buckets[0].Routes[0].Caller!.Length);

        // Regression: real ARM deployment ids (about 170 characters) come back whole, so routes still match their deployments.
        const string armId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-llm/providers/Microsoft.CognitiveServices/accounts/oai-germanywestcentral/deployments/llm-gpt-4omini-public";
        var whole = new DashboardHistory();
        whole.Tick(at.AddSeconds(1), [new DashboardRoute("caller", "gpt-4o-mini@2024-07-18", "eu", 200, [new DashboardHop(armId, "Success")], 1)], []);
        Assert.Equal(armId, whole.Query(at.AddSeconds(1), at, at.AddSeconds(1), 1).Buckets[0].Routes[0].Hops[0].DeploymentId);

        // Deployments seen: 1000 new ids per tick.
        var seen = new DashboardHistory();
        for (var tick = 1; tick <= 8; tick++)
            seen.Tick(at.AddSeconds(tick), [], Enumerable.Range(0, 1000).Select(index => Merged($"s{tick}-{index}")).ToArray());
        Assert.Equal(DashboardHistory.MaxDeployments, seen.DeploymentCount);
        var kept = seen.Query(at.AddSeconds(8), at, at.AddSeconds(9), 60).Deployments;
        Assert.DoesNotContain(kept, item => item.Deployment.Id.StartsWith("s3-", StringComparison.Ordinal));
        Assert.Contains(kept, item => item.Deployment.Id.StartsWith("s4-", StringComparison.Ordinal));

        // Requests: count cap (unremarkable ones leave before notable ones), then the byte budget.
        var requests = new DashboardHistory();
        for (var index = 0; index < 100; index++)
            requests.Add(at.AddSeconds(index), Request("normal" + index));
        for (var index = 0; index < 20_005; index++)
            requests.Add(at.AddSeconds(100), Request(index.ToString()) with { Status = 500 });
        Assert.Equal(DashboardHistory.MaxRequests, requests.RequestCount);
        Assert.Equal("5", requests.Retained()[0].Id);
        Assert.Equal(DashboardHistory.MaxRequests, requests.NotableRequestCount);
        Assert.Equal(DashboardHistory.MaxRequests, requests.Requests(at.AddSeconds(100), TimeSpan.FromMinutes(1)).Count);
        var large = new DashboardHistory();
        for (var index = 0; index < 3000; index++)
            large.Add(at, Request(index.ToString()) with { Status = 500, RequestedModel = new string('m', 10_000) });
        Assert.InRange(large.RequestCount, 1000, 1700);
        Assert.Equal("2999", large.Retained()[^1].Id);
    }

    [Fact]
    public void RequestEventsLastOneHour()
    {
        var history = new DashboardHistory();
        var at = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        history.Add(at, Request("old"));
        history.Add(at.AddMinutes(30), Request("new"));
        history.Prune(at.AddHours(1));
        Assert.Equal("new", Assert.Single(history.Retained()).Id);
    }

    [Fact]
    public void AnHourAtFortyRequestsPerSecondKeepsEveryNotableRequestWithinTheCap()
    {
        var history = new DashboardHistory();
        var at = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        for (var second = 0; second < 3600; second++)
            for (var index = 0; index < 40; index++)
            {
                var number = second * 40 + index;
                history.Add(at.AddSeconds(second + index / 40.0), Request(number.ToString()) with { Status = number % 50 == 0 ? 500 : 200 });
            }
        history.Prune(at.AddSeconds(3599.99));
        Assert.Equal(2880, history.NotableRequestCount);
        Assert.InRange(history.RequestCount, 2880 + 3599 * 4, DashboardHistory.MaxRequests);
        var first = history.Retained()[0];
        Assert.Equal("0", first.Id);
        Assert.Equal(4, history.Retained().Count(item => int.Parse(item.Id) / 40 == 1 && item.Status < 400));
        Assert.Equal(4800, history.Requests(at.AddSeconds(3599.99), TimeSpan.FromMinutes(2)).Count);
    }

    [Fact]
    public void ClosedMinuteSummaryClassifiesOutcomesAndStatesAndRidesOnTheClosingDelta()
    {
        var clock = new TestClock();
        var store = new DashboardStore(clock);
        store.Ingest(Batch(clock, "one", "Healthy") with
        {
            State = new(clock.GetUtcNow(), [Deployment("Throttled") with { Id = "a" }, Deployment("Open") with { Id = "b" }, Deployment("Healthy") with { Id = "c" }]),
            Routes = [Route("served", count: 5), Route("retried", hops: 2, count: 2), Route("failed", 503, count: 3), Route("throttled", 429, 2, 1),
                Route("refused", 429, 0, 4), Route("client", 400, count: 6)]
        });
        clock.Advance(TimeSpan.FromSeconds(1));
        var first = store.Tick();
        Assert.Empty(first.Summary);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(store.Tick().Summary);
        clock.Advance(TimeSpan.FromSeconds(1));
        var closing = store.Tick();
        var bucket = Assert.Single(closing.Summary);
        Assert.Equal(new SummaryBucket(first.At.AddSeconds(59), 21, 7, 2, 4, 4, "Open", 2), bucket);
        Assert.Empty(store.Tick().Summary);
        Assert.Equal(bucket, Assert.Single(store.Snapshot().Summary));
    }

    [Theory]
    [InlineData(1, 100, 1)]
    [InlineData(10, 10, 10)]
    [InlineData(60, 2, 60)]
    [InlineData(600, 1, 600)]
    public void HistoryAggregatesRoutesAndStatesPerBucket(int resolution, int buckets, int perBucket)
    {
        var clock = new TestClock();
        var store = new DashboardStore(clock);
        var start = clock.GetUtcNow();
        // Second 1205..1214 (ticks at start+1206..start+1215) is throttled.
        Run(clock, store, 1800, index => index is >= 1205 and < 1215 ? "Throttled" : "Healthy");
        var from = start.AddSeconds(1200);
        var to = from.AddSeconds(resolution == 600 ? 600 : resolution * buckets);
        var history = store.History(from, to, resolution);
        Assert.Equal(buckets, history.Buckets.Count);
        Assert.All(history.Buckets, bucket =>
        {
            Assert.Equal(perBucket, bucket.Seconds);
            Assert.Equal(perBucket, Assert.Single(bucket.Routes).Count);
        });
        var states = history.Buckets.SelectMany(bucket => bucket.States).ToArray();
        Assert.All(states, state => Assert.Equal("Throttled", state.State));
        Assert.Equal(10, states.Sum(state => state.Seconds));
        Assert.Equal(history.Buckets.Select(bucket => bucket.At).Order(), history.Buckets.Select(bucket => bucket.At));
        Assert.Equal(from.AddSeconds(resolution), history.Buckets[0].At);
    }

    [Fact]
    public void BeyondOneHourMinuteResolutionsUseMinuteBucketsAndFineResolutionsClamp()
    {
        var clock = new TestClock();
        var store = new DashboardStore(clock);
        var start = clock.GetUtcNow();
        Run(clock, store, 7200, index => index is >= 600 and < 660 ? "Open" : "Healthy");
        var history = store.History(start, clock.GetUtcNow(), 300);
        Assert.Equal(24, history.Buckets.Count);
        Assert.All(history.Buckets, bucket => Assert.Equal(300, bucket.Seconds));
        Assert.All(history.Buckets, bucket => Assert.Equal(300, bucket.Routes.Sum(route => route.Count)));
        var open = Assert.Single(history.Buckets.SelectMany(bucket => bucket.States));
        Assert.Equal(("Open", 60), (open.State, open.Seconds));
        var fine = store.History(start, clock.GetUtcNow(), 10);
        Assert.Equal(clock.GetUtcNow().AddHours(-1), fine.From);
        Assert.Equal(360, fine.Buckets.Count);
    }

    [Fact]
    public void HistoryReturnsNotableRequestsFirstUpToOneThousandOldestFirst()
    {
        var clock = new TestClock();
        var store = new DashboardStore(clock);
        var start = clock.GetUtcNow();
        for (var tick = 0; tick < 18; tick++)
        {
            store.Ingest(Batch(clock, "one", "Healthy") with
            {
                Requests = Enumerable.Range(0, 100).Select(index => Request($"{tick}-{index}") with { Status = index < 60 ? 500 : 200 })
                    .Concat(tick == 0 ? [Request("retried") with { Attempts = [.. Request("x").Attempts, .. Request("x").Attempts] }] : []).ToArray()
            });
            clock.Advance(TimeSpan.FromSeconds(1));
            store.Tick();
        }
        var requests = store.History(start, clock.GetUtcNow(), 60).Requests;
        Assert.Equal(1000, requests.Count);
        Assert.All(requests, item => Assert.True(item.Status >= 400 || item.Attempts.Count > 1));
        Assert.Equal("0-0", requests[0].Id);
        var ticks = requests.Select(item => item.Id == "retried" ? 0 : int.Parse(item.Id.Split('-')[0])).ToArray();
        Assert.Equal(ticks.Order(), ticks);
        // Three seconds: every notable request, then the four sampled others per second.
        var mixed = store.History(start, start.AddSeconds(3), 1).Requests;
        Assert.Equal(3 * 60 + 1 + 3 * 4, mixed.Count);
        Assert.Empty(store.History(start.AddSeconds(30), start.AddSeconds(40), 1).Requests);
    }

    [Fact]
    public void HistoryListsEveryDeploymentSeenInRangeWithItsLatestRecord()
    {
        var clock = new TestClock();
        var store = new DashboardStore(clock);
        var start = clock.GetUtcNow();
        for (var tick = 0; tick < 20; tick++)
        {
            store.Ingest(Batch(clock, "one", "Healthy") with
            {
                State = new(clock.GetUtcNow(), [Deployment(tick < 5 ? "Healthy" : "Open") with { Id = tick < 10 ? "removed" : "added" }])
            });
            clock.Advance(TimeSpan.FromSeconds(1));
            store.Tick();
        }
        var early = store.History(start, start.AddSeconds(5), 1).Deployments;
        Assert.Equal("removed", Assert.Single(early).Deployment.Id);
        var all = store.History(start, clock.GetUtcNow(), 1).Deployments;
        Assert.Equal(["added", "removed"], all.Select(item => item.Deployment.Id));
        Assert.Equal("Open", all[1].Deployment.State);
        Assert.Equal("added", Assert.Single(store.History(start.AddSeconds(15), clock.GetUtcNow(), 1).Deployments).Deployment.Id);
    }

    [Fact]
    public void ReplicaNumbersTakeTheLowestFreeNumberAndStayStable()
    {
        var clock = new TestClock();
        var store = new DashboardStore(clock);
        foreach (var replica in new[] { "zeta", "alpha", "mid" })
            store.Ingest(Batch(clock, replica, "Healthy"));
        var first = store.Snapshot();
        Assert.Equal("zeta=1 alpha=2 mid=3", Numbers(first));
        Assert.Equal(["zeta", "alpha", "mid"], first.Replicas);
        clock.Advance(TimeSpan.FromSeconds(20));
        store.Ingest(Batch(clock, "zeta", "Healthy"));
        store.Ingest(Batch(clock, "mid", "Healthy"));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("zeta=1 mid=3", Numbers(store.Tick()));
        store.Ingest(Batch(clock, "new", "Healthy"));
        store.Ingest(Batch(clock, "alpha", "Healthy"));
        var frame = store.Snapshot();
        Assert.Equal("zeta=1 new=2 mid=3 alpha=4", Numbers(frame));
        Assert.Equal(["zeta", "new", "mid", "alpha"], frame.Replicas);
        Assert.Equal(["zeta", "new", "mid", "alpha"], Assert.Single(frame.Deployments).Replicas.Select(item => item.Replica));
    }

    private static string Numbers(DashboardFrame frame) =>
        string.Join(' ', frame.ReplicaNumbers.OrderBy(pair => pair.Value).Select(pair => $"{pair.Key}={pair.Value}"));

    [Fact]
    public void SnapshotCarriesOnlyTwoMinutesOfRequestsAndTicks()
    {
        var clock = new TestClock();
        var store = new DashboardStore(clock);
        for (var tick = 0; tick < 600; tick++)
        {
            store.Ingest(Batch(clock, "one", "Healthy") with { Requests = [Request(tick.ToString())], Routes = [Route("dev")] });
            clock.Advance(TimeSpan.FromSeconds(1));
            store.Tick();
        }
        var snapshot = store.Snapshot();
        Assert.Equal(119, snapshot.Requests.Count);
        Assert.Equal(120, snapshot.RouteHistory.Count);
        Assert.Equal(9, snapshot.Summary.Count);
        Assert.Equal(600, store.History(clock.GetUtcNow().AddHours(-1), clock.GetUtcNow(), 60).Requests.Count);
    }
}
