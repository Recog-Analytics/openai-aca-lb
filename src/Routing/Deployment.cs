namespace openai_loadbalancer.Routing;

public readonly record struct ModelKey(string Name, string Version)
{
    public override string ToString() => $"{Name}@{Version}";

    public static ModelKey Parse(string value)
    {
        var parts = value.Split('@');
        return parts.Length == 2 ? new(parts[0], parts[1]) : throw new FormatException("A model key has the form 'name@version'.");
    }
}

public sealed record DiscoveredDeployment(
    string AccountId,
    string AccountName,
    Uri Endpoint,
    string Region,
    string DeploymentName,
    ModelKey Model,
    string Sku,
    decimal Capacity,
    string ProvisioningState);

public sealed record Deployment(
    string AccountId,
    string AccountName,
    Uri Endpoint,
    string Region,
    string DeploymentName,
    ModelKey Model,
    string Sku,
    int Tier,
    string Zone,
    decimal Weight,
    bool Disabled)
{
    public decimal Capacity { get; init; }

    // ARM identity distinguishes accounts with the same name in different scopes.
    public string Id => $"{AccountId.TrimEnd('/')}/deployments/{DeploymentName}";
}
