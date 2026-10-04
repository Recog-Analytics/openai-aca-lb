using openai_loadbalancer.Configuration;
using openai_loadbalancer.Operations;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Health;

public enum DeploymentHealth { Healthy, Throttled, Degraded, Open, Disabled }
public enum HealthOutcome { Success, Failure, AccountFailure, Throttled, Misconfigured, Ignored }

public sealed record DeploymentHealthSnapshot(string DeploymentId, string AccountId, DeploymentHealth State,
    bool AccountOpen, bool CanAttempt, TimeSpan RetryAfter, TimeSpan? P95);

public interface IHealthState
{
    void Reconcile(RoutingTable table, IReadOnlyDictionary<string, ModelHealthOverride> modelOverrides);
    IReadOnlyList<DeploymentHealthSnapshot> GetSnapshot();
    HealthAttempt? TryAcquire(string deploymentId);
}

public sealed class HealthAttempt : IDisposable
{
    private readonly Action<HealthOutcome, string?, string?, TimeSpan?> complete;
    private readonly Action<TimeSpan> recordTtfb;
    private int completed;
    private int latencyRecorded;

    internal HealthAttempt(Action<HealthOutcome, string?, string?, TimeSpan?> complete, Action<TimeSpan> recordTtfb)
    {
        this.complete = complete;
        this.recordTtfb = recordTtfb;
    }

    public void RecordTtfb(TimeSpan ttfb)
    {
        if (ttfb < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttfb));
        if (Volatile.Read(ref completed) == 0 && Interlocked.Exchange(ref latencyRecorded, 1) == 0)
            recordTtfb(ttfb);
    }

    public void Complete(HealthOutcome outcome, string? retryAfterMilliseconds = null, string? retryAfter = null, TimeSpan? ttfb = null)
    {
        if (ttfb < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttfb));
        if (Interlocked.Exchange(ref completed, 1) == 0)
        {
            if (ttfb.HasValue && Interlocked.Exchange(ref latencyRecorded, 1) != 0)
                ttfb = null;
            complete(outcome, retryAfterMilliseconds, retryAfter, ttfb);
        }
    }

    public void Dispose() => Complete(HealthOutcome.Ignored);
}

public sealed class HealthState(TimeProvider timeProvider, IHealthObserver? observer = null) : IHealthState
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> deployments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CircuitBreaker> accounts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ModelHealthOverride> modelOverrides = new(StringComparer.Ordinal);

    public void Reconcile(RoutingTable table, IReadOnlyDictionary<string, ModelHealthOverride> modelOverrides)
    {
        lock (gate)
        {
            EvaluateLatency(timeProvider.GetUtcNow());
            var oldMembers = deployments.Values.GroupBy(entry => entry.Deployment.AccountId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(entry => entry.Deployment.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            this.modelOverrides = modelOverrides.ToDictionary(item => item.Key, item => new ModelHealthOverride
            {
                DegradedFloorSeconds = item.Value.DegradedFloorSeconds,
                DegradedThresholdSeconds = item.Value.DegradedThresholdSeconds
            }, StringComparer.Ordinal);
            var ids = table.Deployments.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var id in deployments.Keys.Where(id => !ids.Contains(id)).ToArray())
                deployments.Remove(id);
            foreach (var deployment in table.Deployments)
            {
                if (!deployments.TryGetValue(deployment.Id, out var entry))
                    deployments.Add(deployment.Id, new(deployment));
                else
                {
                    if (entry.Deployment.Model != deployment.Model)
                        entry.Latency = new();
                    entry.Deployment = deployment;
                    entry.Misconfigured = false;
                }
                accounts.TryAdd(deployment.AccountId, new());
            }
            var accountIds = table.Deployments.Select(item => item.AccountId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var id in accounts.Keys.Where(id => !accountIds.Contains(id)).ToArray())
                accounts.Remove(id);
            foreach (var group in deployments.Values.GroupBy(entry => entry.Deployment.AccountId, StringComparer.OrdinalIgnoreCase))
                if ((!oldMembers.TryGetValue(group.Key, out var previous) || !previous.SetEquals(group.Select(entry => entry.Deployment.Id))) &&
                    !accounts[group.Key].IsOpen && group.Count(entry => entry.Circuit.IsOpen) > group.Count() / 2.0)
                    accounts[group.Key].Open(timeProvider.GetUtcNow());
            EvaluateLatency(timeProvider.GetUtcNow());
        }
    }

    public IReadOnlyList<DeploymentHealthSnapshot> GetSnapshot()
    {
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            var p95s = EvaluateLatency(now);
            return deployments.Values.Select(entry =>
            {
                var account = accounts[entry.Deployment.AccountId];
                var retryAfter = entry.ThrottledUntil > now ? entry.ThrottledUntil - now : TimeSpan.Zero;
                var state = State(entry, now);
                if (account.IsOpen && state != DeploymentHealth.Disabled)
                    state = DeploymentHealth.Open;
                return new DeploymentHealthSnapshot(entry.Deployment.Id, entry.Deployment.AccountId, state,
                    account.IsOpen, CanAcquire(entry, account, now), retryAfter, p95s[entry.Deployment.Id]);
            }).ToArray();
        }
    }

    public HealthAttempt? TryAcquire(string deploymentId)
    {
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            EvaluateLatency(now);
            if (!deployments.TryGetValue(deploymentId, out var entry))
                return null;
            var account = accounts[entry.Deployment.AccountId];
            if (!CanAcquire(entry, account, now))
                return null;
            var deploymentProbe = entry.Circuit.Acquire();
            var accountProbe = account.Acquire();
            var latency = entry.Latency;
            return new((outcome, milliseconds, retryAfter, ttfb) => Complete(entry, latency, account, deploymentProbe,
                accountProbe, outcome, milliseconds, retryAfter, ttfb), ttfb => RecordTtfb(entry, latency, ttfb));
        }
    }

    private void RecordTtfb(Entry entry, DegradationTracker latency, TimeSpan ttfb)
    {
        lock (gate)
        {
            if (deployments.TryGetValue(entry.Deployment.Id, out var current) && ReferenceEquals(entry, current) &&
                ReferenceEquals(latency, entry.Latency))
            {
                var now = timeProvider.GetUtcNow();
                entry.Latency.Record(now, ttfb);
                EvaluateLatency(now);
            }
        }
    }

    private void Complete(Entry entry, DegradationTracker latency, CircuitBreaker account, long? deploymentProbe, long? accountProbe,
        HealthOutcome outcome, string? milliseconds, string? retryAfter, TimeSpan? ttfb)
    {
        lock (gate)
        {
            // A completion from a removed deployment cannot update a later incarnation of its ARM ID.
            var now = timeProvider.GetUtcNow();
            EvaluateLatency(now);
            if (!deployments.TryGetValue(entry.Deployment.Id, out var current) || !ReferenceEquals(entry, current))
            {
                // Release an account probe when siblings survive removal of its deployment.
                account.Complete(now, accountProbe, null);
                return;
            }
            if (ttfb.HasValue && ReferenceEquals(latency, entry.Latency))
                entry.Latency.Record(now, ttfb.Value);
            if (outcome == HealthOutcome.Throttled)
                entry.ThrottledUntil = Max(entry.ThrottledUntil, now + RetryAfterParser.Parse(milliseconds, retryAfter, now));
            if (outcome == HealthOutcome.Misconfigured)
                entry.Misconfigured = true;
            bool? failed = outcome switch
            {
                HealthOutcome.Success => false,
                HealthOutcome.Failure => true,
                _ => null
            };
            var wasOpen = entry.Circuit.IsOpen;
            entry.Circuit.Complete(now, deploymentProbe, failed);
            if (accountProbe.HasValue)
                account.Complete(now, accountProbe, outcome == HealthOutcome.AccountFailure ? true : failed);
            if (outcome == HealthOutcome.AccountFailure && !accountProbe.HasValue && !account.IsOpen)
                account.Open(now);
            if (!wasOpen && entry.Circuit.IsOpen && !account.IsOpen)
            {
                var siblings = deployments.Values.Where(item => string.Equals(item.Deployment.AccountId,
                    entry.Deployment.AccountId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (siblings.Count(item => item.Circuit.IsOpen) > siblings.Length / 2.0)
                    account.Open(now);
            }
            EvaluateLatency(now);
        }
    }

    private Dictionary<string, TimeSpan?> EvaluateLatency(DateTimeOffset now)
    {
        var p95s = deployments.Values.ToDictionary(entry => entry.Deployment.Id, entry => entry.Latency.GetP95(now),
            StringComparer.OrdinalIgnoreCase);
        foreach (var group in deployments.Values.GroupBy(entry => entry.Deployment.Model))
            foreach (var entry in group)
            {
                var peers = group.Where(peer => !ReferenceEquals(peer, entry) && p95s[peer.Deployment.Id].HasValue)
                    .Select(peer => p95s[peer.Deployment.Id]!.Value.TotalSeconds).Order().ToArray();
                double? median = peers.Length == 0 ? null :
                    (peers[(peers.Length - 1) / 2] + peers[peers.Length / 2]) / 2;
                var config = modelOverrides.GetValueOrDefault(group.Key.ToString());
                entry.Latency.Evaluate(now, p95s[entry.Deployment.Id], median, config?.DegradedFloorSeconds ?? 2,
                    config?.DegradedThresholdSeconds);
            }
        ObserveTransitions(now);
        return p95s;
    }

    private void ObserveTransitions(DateTimeOffset now)
    {
        foreach (var entry in deployments.Values)
        {
            var state = State(entry, now);
            if (accounts[entry.Deployment.AccountId].IsOpen && state != DeploymentHealth.Disabled)
                state = DeploymentHealth.Open;
            if (state == entry.ObservedState && entry.Misconfigured == entry.ObservedMisconfigured)
                continue;
            var transition = new HealthTransition(entry.Deployment, entry.ObservedState, state,
                entry.Misconfigured, entry.ThrottledUntil > now ? entry.ThrottledUntil - now : TimeSpan.Zero);
            entry.ObservedState = state;
            entry.ObservedMisconfigured = entry.Misconfigured;
            // Operations must not prevent health updates or request forwarding.
            try { observer?.OnTransition(transition); }
            catch (Exception) { }
        }
    }

    private static DeploymentHealth State(Entry entry, DateTimeOffset now) =>
        entry.Deployment.Disabled || entry.Misconfigured ? DeploymentHealth.Disabled :
        entry.Circuit.IsOpen ? DeploymentHealth.Open :
        entry.ThrottledUntil > now ? DeploymentHealth.Throttled :
        entry.Latency.IsDegraded ? DeploymentHealth.Degraded : DeploymentHealth.Healthy;

    private static bool CanAcquire(Entry entry, CircuitBreaker account, DateTimeOffset now) =>
        !entry.Deployment.Disabled && !entry.Misconfigured && entry.ThrottledUntil <= now &&
        entry.Circuit.CanAcquire(now) && account.CanAcquire(now);

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;

    private sealed class Entry(Deployment deployment)
    {
        public Deployment Deployment { get; set; } = deployment;
        public CircuitBreaker Circuit { get; } = new();
        public DegradationTracker Latency { get; set; } = new();
        public DateTimeOffset ThrottledUntil { get; set; }
        public bool Misconfigured { get; set; }
        public DeploymentHealth ObservedState { get; set; } = DeploymentHealth.Healthy;
        public bool ObservedMisconfigured { get; set; }
    }
}
