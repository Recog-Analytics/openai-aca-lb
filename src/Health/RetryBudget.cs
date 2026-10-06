namespace openai_loadbalancer.Health;

public interface IRetryBudget
{
    void RecordRequest();
    bool TryAcquireRetry();
}

public sealed class RetryBudget(TimeProvider timeProvider) : IRetryBudget
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private readonly object gate = new();
    private readonly Queue<DateTimeOffset> requests = new();
    private readonly Queue<DateTimeOffset> retries = new();

    // Record each original request once. Retries do not increase the request count.
    public void RecordRequest()
    {
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            RemoveExpired(now);
            requests.Enqueue(now);
        }
    }

    public bool TryAcquireRetry()
    {
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            RemoveExpired(now);
            var limit = Math.Max(100, requests.Count / 5);
            if (retries.Count >= limit)
                return false;
            retries.Enqueue(now);
            return true;
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        var cutoff = now - Window;
        while (requests.TryPeek(out var requestedAt) && requestedAt <= cutoff)
            requests.Dequeue();
        while (retries.TryPeek(out var retriedAt) && retriedAt <= cutoff)
            retries.Dequeue();
    }
}
