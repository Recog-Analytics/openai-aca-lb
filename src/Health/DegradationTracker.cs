namespace openai_loadbalancer.Health;

internal sealed class DegradationTracker
{
    private readonly Queue<(DateTimeOffset At, TimeSpan Ttfb)> samples = new();
    private DateTimeOffset? recoveringSince;
    public bool IsDegraded { get; private set; }

    public void Record(DateTimeOffset now, TimeSpan ttfb) => samples.Enqueue((now, ttfb));

    public TimeSpan? GetP95(DateTimeOffset now)
    {
        while (samples.TryPeek(out var sample) && sample.At <= now - TimeSpan.FromMinutes(5))
            samples.Dequeue();
        if (samples.Count < 20)
            return null;
        var sorted = samples.Select(sample => sample.Ttfb).Order().ToArray();
        return sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
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
