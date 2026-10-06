using System.Collections.Frozen;

namespace openai_loadbalancer.Routing;

public sealed class RegionGeography
{
    private readonly FrozenDictionary<string, string> geographyGroups;

    public RegionGeography(IReadOnlyDictionary<string, string> geographyGroups)
    {
        this.geographyGroups = geographyGroups.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public string GetZone(string region)
    {
        if (!TryGetZone(region, out var zone))
            throw new ArgumentException($"No geography is configured for region '{region}'.", nameof(region));

        return zone;
    }

    public bool TryGetZone(string region, out string zone)
    {
        zone = "";
        if (!geographyGroups.TryGetValue(region, out var geography) || string.IsNullOrWhiteSpace(geography))
            return false;

        zone = geography.Trim().ToLowerInvariant() switch
        {
            "europe" => "eu",
            "united states" => "us",
            var other => string.Join('-', other.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        };
        return true;
    }
}
