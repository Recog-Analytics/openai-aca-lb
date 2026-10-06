using System.Threading.Channels;
using openai_loadbalancer.Operations;

namespace openai_loadbalancer.Dashboard.Server;

public sealed record ReplicaDeployment(string Replica, string State, double? P95TtfbMs, bool AccountOpen);
public sealed record MergedDeployment(DashboardDeployment Deployment, IReadOnlyList<ReplicaDeployment> Replicas);
public sealed record DeploymentRate(string DeploymentId, double RequestsPerSecond, IReadOnlyList<DashboardCount> Outcomes);
/// <summary>Unsampled route counts received during one tick, ending at <see cref="At"/>.</summary>
public sealed record RouteTick(DateTimeOffset At, IReadOnlyList<DashboardRoute> Routes);

/// <summary>
/// One SSE frame. <see cref="Routes"/> holds this tick's unsampled route counts; only the snapshot fills
/// <see cref="RouteHistory"/> with the last two minutes of ticks, so a new browser can show traffic shares at once.
/// The snapshot's <see cref="Summary"/> holds every retained minute; a delta carries only the minute its tick closed.
/// </summary>
public sealed record DashboardFrame(DateTimeOffset At, IReadOnlyList<string> Replicas,
    IReadOnlyList<MergedDeployment> Deployments, IReadOnlyList<RequestRecord> Requests, IReadOnlyList<DeploymentRate> Counts,
    IReadOnlyList<DashboardRoute> Routes, IReadOnlyList<RouteTick> RouteHistory, IReadOnlyList<SummaryBucket> Summary,
    HistoryRetention Retention, IReadOnlyDictionary<string, int> ReplicaNumbers);

public sealed class DashboardStore(TimeProvider clock)
{
    /// <summary>Request events each delta samples for browsers, across all replicas.</summary>
    public const int BrowserSamplesPerSecond = 100;
    private readonly Lock gate = new();
    private static readonly TimeSpan Recent = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, ReplicaState> replicas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> numbers = new(StringComparer.Ordinal);
    private readonly DashboardHistory history = new();
    private readonly List<RequestRecord> pending = [];
    private long pendingSeen;
    private readonly Dictionary<(string DeploymentId, string Outcome), long> counts = new();
    private readonly Dictionary<string, DashboardRoute> routes = new(StringComparer.Ordinal);
    private readonly HashSet<Channel<DashboardFrame>> subscribers = [];
    private DateTimeOffset lastTick = clock.GetUtcNow();
    private IReadOnlyList<DeploymentRate> lastRates = [];

    public void Ingest(DashboardBatch batch)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            Prune(now);
            if (!replicas.TryGetValue(batch.Replica, out var previous) || batch.SentAt >= previous.SentAt)
                replicas[batch.Replica] = new(now, batch.SentAt, batch.State);
            if (!numbers.ContainsKey(batch.Replica))
            {
                // The lowest number no other known replica holds, kept while the replica is known.
                var used = numbers.Values.ToHashSet();
                numbers[batch.Replica] = Enumerable.Range(1, used.Count + 1).First(number => !used.Contains(number));
            }
            foreach (var request in batch.Requests)
            {
                history.Add(now, request);
                // Reservoir sampling bounds pending events across all replicas between ticks.
                pendingSeen++;
                if (pending.Count < BrowserSamplesPerSecond)
                    pending.Add(request);
                else
                {
                    var index = Random.Shared.NextInt64(pendingSeen);
                    if (index < BrowserSamplesPerSecond)
                        pending[(int)index] = request;
                }
            }
            foreach (var count in batch.Counts)
            {
                var key = (count.DeploymentId, count.Outcome);
                counts[key] = counts.GetValueOrDefault(key) + count.Count;
            }
            foreach (var route in batch.Routes ?? [])
            {
                var key = DashboardRoute.KeyOf(route);
                routes[key] = routes.TryGetValue(key, out var existing) ? existing with { Count = existing.Count + route.Count } : route;
            }
        }
    }

    public DashboardFrame Snapshot()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            Prune(now);
            return Frame(now, Merge(), history.Requests(now, Recent), [], history.Ticks(now, Recent), history.Summary());
        }
    }

    public DashboardFrame Tick()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            Prune(now);
            var seconds = Math.Max((now - lastTick).TotalSeconds, 0.001);
            lastRates = counts.Select(pair => new DashboardCount(pair.Key.DeploymentId, pair.Key.Outcome, pair.Value))
                .GroupBy(item => item.DeploymentId, StringComparer.OrdinalIgnoreCase)
                .Select(group => new DeploymentRate(group.Key, group.Sum(item => item.Count) / seconds, group.ToArray())).ToArray();
            var deployments = Merge();
            var tickRoutes = routes.Values.ToArray();
            var closed = history.Tick(now, tickRoutes, deployments);
            var frame = Frame(now, deployments, pending.ToArray(), tickRoutes, [], closed == null ? [] : [closed]);
            pending.Clear();
            pendingSeen = 0;
            counts.Clear();
            routes.Clear();
            lastTick = now;
            foreach (var subscriber in subscribers)
                subscriber.Writer.TryWrite(frame);
            return frame;
        }
    }

    public HistoryResponse History(DateTimeOffset from, DateTimeOffset to, int resolution)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            Prune(now);
            return history.Query(now, from, to, resolution);
        }
    }

    public (DashboardFrame Snapshot, Channel<DashboardFrame> Channel) Subscribe()
    {
        lock (gate)
        {
            var channel = Channel.CreateBounded<DashboardFrame>(new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true
            });
            subscribers.Add(channel);
            return (Snapshot(), channel);
        }
    }

    public void Unsubscribe(Channel<DashboardFrame> channel)
    {
        lock (gate)
        {
            subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var replica in replicas.Where(pair => now - pair.Value.ReceivedAt >= TimeSpan.FromSeconds(30)).Select(pair => pair.Key).ToArray())
        {
            replicas.Remove(replica);
            numbers.Remove(replica);
        }
        history.Prune(now);
    }

    private DashboardFrame Frame(DateTimeOffset now, IReadOnlyList<MergedDeployment> deployments, IReadOnlyList<RequestRecord> requests,
        IReadOnlyList<DashboardRoute> tickRoutes, IReadOnlyList<RouteTick> ticks, IReadOnlyList<SummaryBucket> summary) =>
        new(now, replicas.Keys.OrderBy(replica => numbers[replica]).ToArray(), deployments, requests, lastRates, tickRoutes, ticks, summary,
            history.Retention(now), new Dictionary<string, int>(numbers, StringComparer.Ordinal));

    private IReadOnlyList<MergedDeployment> Merge() => replicas.SelectMany(replica => replica.Value.State.Deployments.Select(deployment =>
            (Replica: replica.Key, Deployment: deployment)))
            .GroupBy(item => item.Deployment.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var worst = group.OrderByDescending(item => Severity(item.Deployment.State)).First().Deployment;
                var p95 = group.Max(item => item.Deployment.P95TtfbMs);
                return new MergedDeployment(worst with { P95TtfbMs = p95, AccountOpen = group.Any(item => item.Deployment.AccountOpen) },
                    group.Select(item => new ReplicaDeployment(item.Replica, item.Deployment.State,
                        item.Deployment.P95TtfbMs, item.Deployment.AccountOpen)).OrderBy(item => numbers[item.Replica]).ToArray());
            }).OrderBy(item => item.Deployment.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    public static int Severity(string state) => state switch
    {
        "Healthy" => 0, "Degraded" => 1, "Throttled" => 2, "Open" => 3, "Disabled" => 4,
        _ => -1
    };

    private sealed record ReplicaState(DateTimeOffset ReceivedAt, DateTimeOffset SentAt, DashboardState State);
}
