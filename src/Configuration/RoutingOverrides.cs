namespace openai_loadbalancer.Configuration;

public sealed class RoutingOverrides
{
    public Dictionary<string, string> DefaultVersions { get; init; } = new();
    public List<DeploymentExclusion> Exclude { get; init; } = new();
    public List<DeploymentOverride> Deployments { get; init; } = new();
    public Dictionary<string, RegionOverride> Regions { get; init; } = new();
    public Dictionary<string, ModelHealthOverride> ModelHealth { get; init; } = new();
}

public sealed class DeploymentExclusion
{
    public required string Account { get; init; }
    public string? Deployment { get; init; }
}

public sealed class DeploymentOverride
{
    public required string Account { get; init; }
    public required string Deployment { get; init; }
    public int? Tier { get; init; }
    public decimal WeightMultiplier { get; init; } = 1;
    public bool Disabled { get; init; }
}

public sealed class RegionOverride
{
    public required string Zone { get; init; }
}

public sealed class ModelHealthOverride
{
    public double DegradedFloorSeconds { get; init; } = 2;
    public double? DegradedThresholdSeconds { get; init; }
}
