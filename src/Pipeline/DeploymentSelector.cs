using openai_loadbalancer.Health;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Pipeline;

public sealed record SelectionResult(Deployment? Deployment, HealthAttempt? Attempt, TimeSpan? RetryAfter);

public interface IDeploymentSelector
{
    SelectionResult Select(RoutingTable table, RoutingPool pool, string zone, IReadOnlySet<string> tried);
}

public interface ISelectionRandom
{
    double NextDouble();
}

public sealed class SelectionRandom : ISelectionRandom
{
    public double NextDouble() => Random.Shared.NextDouble();
}

public sealed class DeploymentSelector(IHealthState health, ISelectionRandom? random = null) : IDeploymentSelector
{
    private readonly ISelectionRandom random = random ?? new SelectionRandom();

    public SelectionResult Select(RoutingTable table, RoutingPool pool, string zone, IReadOnlySet<string> tried)
    {
        var triedIds = new HashSet<string>(tried, StringComparer.OrdinalIgnoreCase);
        var candidates = table.GetDeployments(pool).Where(deployment =>
            !deployment.Disabled && deployment.Weight > 0 && !triedIds.Contains(deployment.Id) &&
            (zone == "global" || deployment.Zone == zone)).ToArray();
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool? probeDegraded = triedIds.Count == 0 ? null : false;
        while (true)
        {
            var states = health.GetSnapshot().ToDictionary(state => state.DeploymentId, StringComparer.OrdinalIgnoreCase);
            var available = candidates.Where(deployment => !rejected.Contains(deployment.Id) &&
                states.TryGetValue(deployment.Id, out var state) && state.CanAttempt).ToArray();
            var degraded = available.Where(deployment => states[deployment.Id].State == DeploymentHealth.Degraded).ToArray();
            var healthy = available.Where(deployment => states[deployment.Id].State != DeploymentHealth.Degraded).ToArray();
            if (degraded.Length > 0)
                probeDegraded ??= random.NextDouble() < 0.05;
            var group = degraded.Length > 0 && (probeDegraded == true || healthy.Length == 0) ? degraded : healthy;
            if (group.Length == 0)
            {
                var throttles = candidates.Where(deployment => states.TryGetValue(deployment.Id, out var state) &&
                    state.State == DeploymentHealth.Throttled && !state.AccountOpen && state.RetryAfter > TimeSpan.Zero)
                    .Select(deployment => states[deployment.Id].RetryAfter).ToArray();
                return new(null, null, throttles.Length == 0 ? null : throttles.Min());
            }

            var tier = group.Min(deployment => deployment.Tier);
            var deployment = Pick(group.Where(deployment => deployment.Tier == tier).ToArray());
            var attempt = health.TryAcquire(deployment.Id);
            if (attempt != null)
                return new(deployment, attempt, null);
            // Another request can claim a half-open probe after the snapshot. Try each remaining ID at most once.
            rejected.Add(deployment.Id);
        }
    }

    private Deployment Pick(Deployment[] deployments)
    {
        var target = random.NextDouble() * deployments.Sum(deployment => (double)deployment.Weight);
        foreach (var deployment in deployments)
        {
            target -= (double)deployment.Weight;
            if (target < 0)
                return deployment;
        }
        return deployments[^1];
    }
}
