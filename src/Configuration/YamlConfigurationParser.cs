using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace openai_loadbalancer.Configuration;

public interface IYamlConfigurationParser
{
    RoutingOverrides ParseOverrides(string yaml);
    CallersConfiguration ParseCallers(string yaml);
}

public sealed class YamlConfigurationParser : IYamlConfigurationParser
{
    private readonly IDeserializer deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .WithEnforceNullability()
        .WithEnforceRequiredMembers()
        .Build();

    public RoutingOverrides ParseOverrides(string yaml)
    {
        var config = deserializer.Deserialize<RoutingOverrides>(yaml) ?? new RoutingOverrides();
        foreach (var (model, version) in config.DefaultVersions)
        {
            RequireText(model, "Default model name");
            RequireText(version, "Default model version");
        }
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, target) in config.Aliases)
        {
            RequireText(alias, "Alias name");
            if (alias.Contains('@'))
                throw new ArgumentException($"Alias '{alias}' cannot contain '@'.");
            if (!aliases.Add(alias))
                throw new ArgumentException($"Alias '{alias}' is defined more than once (aliases are case-insensitive).");
            var parts = (target ?? "").Split('@');
            if (parts.Length > 2 || parts.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException($"Alias '{alias}' must target 'name' or 'name@version'.");
        }
        foreach (var exclusion in config.Exclude)
        {
            RequireText(exclusion.Account, "Excluded account");
            if (exclusion.Deployment != null)
                RequireText(exclusion.Deployment, "Excluded deployment");
        }
        foreach (var item in config.Deployments)
        {
            RequireText(item.Account, "Override account");
            RequireText(item.Deployment, "Override deployment");
            if (item.Tier is < 0 or > 2)
                throw new ArgumentException("Override tier must be 0, 1 or 2.");
            if (item.WeightMultiplier < 0)
                throw new ArgumentException("Weight multiplier cannot be negative.");
        }
        if (config.Deployments.GroupBy(item => (item.Account.ToLowerInvariant(), item.Deployment.ToLowerInvariant()))
            .Any(group => group.Count() > 1))
            throw new ArgumentException("A deployment can have only one override.");
        foreach (var (region, item) in config.Regions)
        {
            RequireText(region, "Override region");
            RequireText(item.Zone, "Override zone");
        }
        foreach (var (model, item) in config.ModelHealth)
        {
            var parts = model.Split('@');
            if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Model health overrides require 'name@version' keys.");
            if (!double.IsFinite(item.DegradedFloorSeconds) || item.DegradedFloorSeconds <= 0 ||
                (item.DegradedThresholdSeconds.HasValue &&
                 (!double.IsFinite(item.DegradedThresholdSeconds.Value) || item.DegradedThresholdSeconds <= 0)))
                throw new ArgumentException("Model health thresholds must be finite, positive seconds.");
        }
        return config;
    }

    public CallersConfiguration ParseCallers(string yaml)
    {
        var config = deserializer.Deserialize<CallersConfiguration>(yaml) ?? new CallersConfiguration();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var caller in config.Callers)
        {
            RequireText(caller.Name, "Caller name");
            if (!names.Add(caller.Name))
                throw new ArgumentException("Caller names must be unique.");
            if (caller.Zones.Count == 0 || caller.Zones.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Each caller must have at least one non-empty zone.");
            if (caller.KeyHashes.Count == 0)
                throw new ArgumentException("Each caller must have at least one key hash.");
            foreach (var hash in caller.KeyHashes)
            {
                if (hash == null || !hash.StartsWith("sha256:", StringComparison.Ordinal) ||
                    hash.Length != 71 || hash.AsSpan(7).ContainsAnyExcept("0123456789abcdefABCDEF"))
                    throw new ArgumentException("Caller key hashes must use 'sha256:' and 64 hexadecimal digits.");
                if (!hashes.Add(hash))
                    throw new ArgumentException("A key hash can belong to only one caller and occur only once.");
            }
        }
        return config;
    }

    private static void RequireText(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{field} cannot be empty.");
    }
}
