namespace openai_loadbalancer.Operations;

public sealed class RequestHistory(openai_loadbalancer.Dashboard.DashboardEventBuffer? dashboard = null)
{
    public const int Capacity = 500;
    public const int MaximumModelLength = 512;
    private readonly Lock gate = new();
    private readonly RequestRecord?[] records = new RequestRecord[Capacity];
    private int next;
    private int count;

    public void Add(RequestRecord record)
    {
        // Unknown v1 model identifiers come from the caller's buffered body.
        var snapshot = record with
        {
            RequestedModel = record.RequestedModel is { Length: > MaximumModelLength } model
                ? model[..MaximumModelLength] + "…" : record.RequestedModel,
            Attempts = Array.AsReadOnly(record.Attempts.ToArray())
        };
        lock (gate)
        {
            records[next] = snapshot;
            next = (next + 1) % Capacity;
            count = Math.Min(count + 1, Capacity);
        }
        dashboard?.Add(snapshot);
    }

    public IReadOnlyList<RequestRecord> GetRecent(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        lock (gate)
        {
            var snapshot = new RequestRecord[count];
            for (var index = 0; index < count; index++)
                snapshot[index] = records[(next - index - 1 + Capacity) % Capacity]!;
            return Array.AsReadOnly(snapshot.OrderByDescending(record => record.StartedAt)
                .Take(Math.Min(limit, Capacity)).ToArray());
        }
    }
}
