using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace openai_loadbalancer.Devkit;

public sealed record Scenario
{
    public string Subscription { get; init; } = "00000000-0000-0000-0000-000000000001";
    public string ResourceGroup { get; init; } = "devkit";
    public List<AccountDefinition> Accounts { get; init; } = [];

    public static Scenario Load(string path)
    {
        var scenario = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithDuplicateKeyChecking().Build().Deserialize<Scenario>(File.ReadAllText(path));
        if (scenario == null || !Guid.TryParse(scenario.Subscription, out _) ||
            string.IsNullOrWhiteSpace(scenario.ResourceGroup) || scenario.Accounts.Count == 0 ||
            scenario.Accounts.Select(account => account.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != scenario.Accounts.Count ||
            scenario.Accounts.Where(account => account.Port != 0).GroupBy(account => account.Port).Any(group => group.Count() > 1))
            throw new ArgumentException("Invalid devkit scenario.");
        foreach (var account in scenario.Accounts)
        {
            if (!ValidName(account.Name) || string.IsNullOrWhiteSpace(account.Region) ||
                string.IsNullOrWhiteSpace(account.Geography) || account.Port is < 0 or > 65535 ||
                account.Deployments.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != account.Deployments.Count)
                throw new ArgumentException("Invalid devkit account.");
            foreach (var deployment in account.Deployments)
                deployment.Validate();
        }
        return scenario;
    }

    internal static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

public sealed record AccountDefinition
{
    public string Name { get; init; } = "";
    public string Region { get; init; } = "";
    public string Geography { get; init; } = "";
    public int Port { get; init; }
    public List<DeploymentDefinition> Deployments { get; init; } = [];
}

public sealed record DeploymentDefinition
{
    public string Name { get; init; } = "";
    public string Model { get; init; } = "";
    public string Version { get; init; } = "";
    public string Sku { get; init; } = "Standard";
    public decimal Capacity { get; init; } = 100;

    public void Validate()
    {
        if (!Scenario.ValidName(Name) || string.IsNullOrWhiteSpace(Model) || string.IsNullOrWhiteSpace(Version) ||
            string.IsNullOrWhiteSpace(Sku) || Capacity <= 0)
            throw new ArgumentException("Deployment requires a valid name, model, version, SKU, and positive capacity.");
    }
}

public sealed record ControlSettings
{
    public int TtfbMs { get; init; } = 80;
    public int JitterMs { get; init; } = 20;
    public double TokensPerSecond { get; init; } = 30;
    public int OutputTokens { get; init; } = 20;
    public double ErrorRate { get; init; }
    public double ThrottleRate { get; init; }
    public int RetryAfterMs { get; init; } = 10000;
    public bool Outage { get; init; }
    public bool Missing { get; init; }

    public void Validate()
    {
        if (TtfbMs is < 0 or > 120000 || JitterMs is < 0 or > 120000 ||
            !double.IsFinite(TokensPerSecond) || TokensPerSecond is < 1 or > 10000 ||
            OutputTokens is < 1 or > 4096 || RetryAfterMs is < 0 or > 120000 ||
            !double.IsFinite(ErrorRate) || ErrorRate is < 0 or > 1 ||
            !double.IsFinite(ThrottleRate) || ThrottleRate is < 0 or > 1 || ErrorRate + ThrottleRate > 1)
            throw new ArgumentException("Controls contain an invalid delay, token count, pace, or rate.");
    }
}

public sealed record DeploymentSnapshot(string Name, string Model, string Version, string Sku, decimal Capacity,
    ControlSettings Controls, ControlSettings? Overrides);
public sealed record AccountSnapshot(string Name, string Region, int Port, bool Outage,
    ControlSettings Controls, IReadOnlyList<DeploymentSnapshot> Deployments);
public sealed record ControlUpdate(string Account, string? Deployment, ControlSettings? Controls);
