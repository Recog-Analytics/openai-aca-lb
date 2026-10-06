namespace openai_loadbalancer.Health;

internal sealed class DegradationTracker
{
    private readonly Queue<(DateTimeOffset At, TimeSpan Ttfb)> samples = new();
    // The same samples kept in order, so the nearest-rank p95 is one index: health reads it on every snapshot and attempt,
    // under the health lock. Insert and expiry are a binary search and one move, instead of a full sort per read.
    private readonly List<long> sorted = [];
    private DateTimeOffset? recoveringSince;
    public bool IsDegraded { get; private set; }

    public void Record(DateTimeOffset now, TimeSpan ttfb)
    {
        samples.Enqueue((now, ttfb));
        var index = sorted.BinarySearch(ttfb.Ticks);
        sorted.Insert(index < 0 ? ~index : index, ttfb.Ticks);
    }

    public TimeSpan? GetP95(DateTimeOffset now)
    {
        while (samples.TryPeek(out var sample) && sample.At <= now - TimeSpan.FromMinutes(5))
        {
            samples.Dequeue();
            sorted.RemoveAt(sorted.BinarySearch(sample.Ttfb.Ticks));
        }
        if (sorted.Count < 20)
            return null;
        return TimeSpan.FromTicks(sorted[(int)Math.Ceiling(sorted.Count * 0.95) - 1]);
    }

    public void Evaluate(DateTimeOffset now, TimeSpan? p95, double? peerMedianSeconds, double floorSeconds, double? thresholdSeconds)
    {
        if (!p95.HasValue || (!peerMedianSeconds.HasValue && !thresholdSeconds.HasValue))
        {
            recoveringSince = null;
            return;
        }
        var seconds = p95.Value.TotalSeconds;
        var bad = peerMedianSeconds.HasValue
            ? seconds > 2 * peerMedianSeconds && seconds > floorSeconds
            : seconds > thresholdSeconds;
        if (!IsDegraded)
        {
            IsDegraded = bad;
            return;
        }
        var recovered = peerMedianSeconds.HasValue ? seconds < 1.5 * peerMedianSeconds : seconds < 0.75 * thresholdSeconds;
        if (!recovered)
            recoveringSince = null;
        else
        {
            recoveringSince ??= now;
            if (now - recoveringSince >= TimeSpan.FromMinutes(10))
            {
                IsDegraded = false;
                recoveringSince = null;
            }
        }
    }
}
