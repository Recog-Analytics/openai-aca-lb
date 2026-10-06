using System.Diagnostics.Metrics;
using openai_loadbalancer.Operations;

namespace openai_loadbalancer.Dashboard;

public sealed class DashboardEventBuffer : IDisposable
{
    public const int Capacity = 10_000;
    public const int BatchLimit = 200;
    public const string MeterName = "openai_loadbalancer.dashboard";
    private readonly Lock gate = new();
    private readonly Queue<RequestRecord> requests = new();
    private readonly Dictionary<(string DeploymentId, string Outcome), long> counts = new();
    private readonly Dictionary<string, DashboardRoute> routes = new(StringComparer.Ordinal);
    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> drops;
    private long dropped;

    public DashboardEventBuffer() => drops = meter.CreateCounter<long>("lb.dashboard.dropped_requests");
    public long Dropped => Interlocked.Read(ref dropped);

    public void Add(RequestRecord record)
    {
        lock (gate)
        {
            foreach (var attempt in record.Attempts)
                if (attempt.DeploymentId is { } id)
                {
                    var key = (id, attempt.Outcome);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            var route = DashboardRoute.From(record);
            var routeKey = DashboardRoute.KeyOf(route);
            routes[routeKey] = routes.TryGetValue(routeKey, out var existing) ? existing with { Count = existing.Count + 1 } : route;
            if (requests.Count == Capacity)
            {
                requests.Dequeue();
                Interlocked.Increment(ref dropped);
                drops.Add(1);
            }
            requests.Enqueue(record);
        }
    }

    public (IReadOnlyList<RequestRecord> Requests, IReadOnlyList<DashboardCount> Counts, IReadOnlyList<DashboardRoute> Routes) Drain()
    {
        RequestRecord[] pending;
        DashboardCount[] totals;
        DashboardRoute[] paths;
        lock (gate)
        {
            pending = requests.ToArray();
            requests.Clear();
            totals = counts.Select(pair => new DashboardCount(pair.Key.DeploymentId, pair.Key.Outcome, pair.Value)).ToArray();
            counts.Clear();
            paths = routes.Values.ToArray();
            routes.Clear();
        }
        // Uniform sampling keeps busy callers from monopolizing the displayed attempt chains.
        if (pending.Length > BatchLimit)
            Random.Shared.Shuffle(pending);
        return (pending.Take(BatchLimit).ToArray(), totals, paths);
    }

    public void Dispose() => meter.Dispose();
}
