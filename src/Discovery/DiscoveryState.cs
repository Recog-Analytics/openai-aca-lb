using openai_loadbalancer.Configuration;
using openai_loadbalancer.Health;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Discovery;

public sealed record DiscoveredCaller(string Name, IReadOnlyList<string> Zones, IReadOnlyList<string> KeyHashes);

public sealed class DiscoverySnapshot
{
    public RoutingTable Table { get; }
    public IReadOnlyList<DiscoveredCaller> Callers { get; }
    public DateTimeOffset RefreshedAt { get; }

    public DiscoverySnapshot(RoutingTable table, CallersConfiguration callers, DateTimeOffset refreshedAt)
    {
        Table = table;
        Callers = Array.AsReadOnly(callers.Callers.Select(caller => new DiscoveredCaller(caller.Name,
            Array.AsReadOnly(caller.Zones.ToArray()), Array.AsReadOnly(caller.KeyHashes.ToArray()))).ToArray());
        RefreshedAt = refreshedAt;
    }
}

public sealed class DiscoveryState(IHealthState health)
{
    private DiscoverySnapshot? current;

    public DiscoverySnapshot? Current => Volatile.Read(ref current);
    public bool IsReady => Current != null;

    internal void Publish(DiscoverySnapshot snapshot, RoutingOverrides overrides)
    {
        health.Reconcile(snapshot.Table, overrides.ModelHealth);
        Interlocked.Exchange(ref current, snapshot);
    }
}
