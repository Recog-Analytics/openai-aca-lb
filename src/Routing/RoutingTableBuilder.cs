using openai_loadbalancer.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace openai_loadbalancer.Routing;

public interface IRoutingTableBuilder
{
    RoutingTable Build(IEnumerable<DiscoveredDeployment> deployments, RegionGeography geography, RoutingOverrides overrides);
}

public sealed class RoutingTableBuilder(ILogger<RoutingTableBuilder>? logger = null) : IRoutingTableBuilder
{
    private readonly ILogger<RoutingTableBuilder> logger = logger ?? NullLogger<RoutingTableBuilder>.Instance;

    public RoutingTable Build(IEnumerable<DiscoveredDeployment> deployments, RegionGeography geography, RoutingOverrides overrides)
    {
        var regions = overrides.Regions.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var result = new List<Deployment>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in deployments)
        {
            var sku = SkuMapping.FromSku(source.Sku);
            if (source.ProvisioningState != "Succeeded" || sku == null ||
                overrides.Exclude.Any(item => Matches(item.Account, source.AccountName) &&
                    (item.Deployment == null || Matches(item.Deployment, source.DeploymentName))))
                continue;

            var zone = regions.TryGetValue(source.Region, out var region) ? region.Zone : null;
            if (zone == null && !geography.TryGetZone(source.Region, out zone))
            {
                logger.LogWarning("Excluding deployment {AccountId}/deployments/{DeploymentName}: region {Region} has no known geography.",
                    source.AccountId, source.DeploymentName, source.Region);
                continue;
            }
            if (string.IsNullOrWhiteSpace(zone))
                throw new ArgumentException("Deployment zone cannot be empty.");

            if (source.Capacity < 0)
                throw new ArgumentException("Deployment capacity cannot be negative.");
            var settings = overrides.Deployments.SingleOrDefault(item =>
                Matches(item.Account, source.AccountName) && Matches(item.Deployment, source.DeploymentName));
            var tier = settings?.Tier ?? sku.Value.Tier;
            var multiplier = settings?.WeightMultiplier ?? 1;
            if (tier is < 0 or > 2 || multiplier < 0)
                throw new ArgumentException("Deployment overrides require tier 0–2 and a non-negative weight multiplier.");

            // A tier override cannot change residency. Global SKUs always stay global.
            if (sku.Value.Global)
                zone = "global";
            var deployment = new Deployment(source.AccountId, source.AccountName, source.Endpoint,
                source.Region, source.DeploymentName, source.Model, source.Sku, tier, zone,
                source.Capacity * multiplier, settings?.Disabled ?? false)
            { Capacity = source.Capacity };
            if (!identities.Add(deployment.Id))
                throw new ArgumentException($"Duplicate deployment '{deployment.Id}'.");
            result.Add(deployment);
        }
        return new RoutingTable(result, overrides.DefaultVersions);
    }

    private static bool Matches(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
