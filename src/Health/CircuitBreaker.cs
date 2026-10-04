namespace openai_loadbalancer.Health;

// The health store serializes access, including acquisition and completion of probes.
internal sealed class CircuitBreaker
{
    private readonly Queue<(DateTimeOffset At, bool Failed)> outcomes = new();
    private TimeSpan backoff = TimeSpan.FromSeconds(30);
    private long generation;
    private bool probing;

    public DateTimeOffset? OpenUntil { get; private set; }
    public bool IsOpen => OpenUntil.HasValue;
    public bool CanAcquire(DateTimeOffset now) => !IsOpen || (now >= OpenUntil && !probing);

    public long? Acquire()
    {
        if (!IsOpen)
            return null;
        probing = true;
        return generation;
    }

    public void Complete(DateTimeOffset now, long? probe, bool? failed)
    {
        if (probe.HasValue)
        {
            if (!probing || probe != generation)
                return;
            probing = false;
            if (failed == true)
            {
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 300));
                Open(now);
            }
            else if (failed == false)
            {
                OpenUntil = null;
                backoff = TimeSpan.FromSeconds(30);
                outcomes.Clear();
                generation++;
            }
            return;
        }
        if (IsOpen || !failed.HasValue)
            return;
        while (outcomes.TryPeek(out var oldest) && oldest.At <= now - TimeSpan.FromSeconds(30))
            outcomes.Dequeue();
        outcomes.Enqueue((now, failed.Value));
        var failures = outcomes.Count(outcome => outcome.Failed);
        var consecutiveFailures = outcomes.Reverse().TakeWhile(outcome => outcome.Failed).Count();
        if (consecutiveFailures >= 3 || (failures >= 5 && failures > outcomes.Count / 2.0))
            Open(now);
    }

    public void Open(DateTimeOffset now)
    {
        OpenUntil = now + backoff;
        probing = false;
        generation++;
    }
}
