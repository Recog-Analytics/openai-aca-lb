namespace openai_loadbalancer.Discovery;

public sealed class DiscoveryOptions
{
    public const string SectionName = "Discovery";

    public string[] Scopes { get; init; } = [];
    public string OverridesFilePath { get; init; } = "";
    public string CallersFilePath { get; init; } = "";
    public string ArmEndpoint { get; init; } = "https://management.azure.com";
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed record DiscoveryScope(string ResourceId, string SubscriptionId)
{
    public static DiscoveryScope Parse(string value)
    {
        if (Guid.TryParse(value, out var subscription))
            return new($"/subscriptions/{subscription:D}", subscription.ToString("D"));

        var parts = value.TrimEnd('/').Split('/');
        if (parts.Length is 3 or 5 && parts[0] == "" &&
            parts[1].Equals("subscriptions", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(parts[2], out subscription) &&
            (parts.Length == 3 || (parts[3].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(parts[4]) && !parts[4].EndsWith('.') &&
                parts[4].All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '(' or ')'))))
        {
            var resourceId = $"/subscriptions/{subscription:D}";
            if (parts.Length == 5)
                resourceId += $"/resourceGroups/{parts[4]}";
            return new(resourceId, subscription.ToString("D"));
        }

        throw new ArgumentException($"Invalid discovery scope '{value}'. Use a subscription ID or a subscription/resource-group ARM ID.");
    }
}
