namespace openai_loadbalancer.Devkit;

public sealed class ScenarioState(Scenario scenario)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, ControlSettings> accountControls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Account, string Deployment), ControlSettings> overrides = new();
    private readonly Dictionary<string, List<DeploymentDefinition>> deployments = scenario.Accounts
        .ToDictionary(account => account.Name, account => account.Deployments.ToList(), StringComparer.OrdinalIgnoreCase);

    public Scenario Scenario { get; } = scenario;
    public string AccountId(string name) => $"/subscriptions/{Scenario.Subscription}/resourceGroups/{Scenario.ResourceGroup}/providers/Microsoft.CognitiveServices/accounts/{name}";

    public IReadOnlyList<AccountSnapshot> Snapshot()
    {
        lock (gate)
            return Scenario.Accounts.Select(account =>
            {
                var controls = accountControls.GetValueOrDefault(account.Name) ?? new ControlSettings();
                var items = deployments[account.Name].Select(deployment =>
                {
                    var replacement = overrides.GetValueOrDefault((account.Name, deployment.Name));
                    return new DeploymentSnapshot(deployment.Name, deployment.Model, deployment.Version,
                        deployment.Sku, deployment.Capacity, replacement ?? controls, replacement);
                }).ToArray();
                return new AccountSnapshot(account.Name, account.Region, account.Port,
                    controls.Outage || items.Any(item => item.Controls.Outage), controls, items);
            }).ToArray();
    }

    public void Update(ControlUpdate update)
    {
        update.Controls?.Validate();
        lock (gate)
        {
            var account = FindAccount(update.Account);
            if (update.Deployment == null)
                accountControls[account.Name] = update.Controls ?? new ControlSettings();
            else
            {
                var deployment = deployments[account.Name].FirstOrDefault(item =>
                    item.Name.Equals(update.Deployment, StringComparison.OrdinalIgnoreCase)) ??
                    throw new KeyNotFoundException("Unknown deployment.");
                var key = (account.Name, deployment.Name);
                if (update.Controls == null) overrides.Remove(key);
                else overrides[key] = update.Controls;
            }
        }
    }

    public void ApplyPreset(string name)
    {
        if (name is not ("recover-all" or "sweden-slow" or "france-throttled" or "eastus2-outage" or "eu-down"))
            throw new KeyNotFoundException("Unknown preset.");
        lock (gate)
        {
            accountControls.Clear();
            overrides.Clear();
            foreach (var account in Scenario.Accounts)
            {
                var controls = new ControlSettings();
                if (name == "sweden-slow" && account.Region == "swedencentral") controls = controls with { TtfbMs = 4000 };
                if (name == "france-throttled" && account.Region == "francecentral") controls = controls with { ThrottleRate = 1 };
                if ((name == "eastus2-outage" && account.Region == "eastus2") ||
                    (name == "eu-down" && account.Geography == "Europe")) controls = controls with { Outage = true };
                accountControls[account.Name] = controls;
            }
        }
    }

    public void PutDeployment(string accountName, string name, DeploymentDefinition deployment)
    {
        deployment.Validate();
        if (deployment.Name != name) throw new ArgumentException("Path and deployment names must match.");
        lock (gate)
        {
            var account = FindAccount(accountName);
            deployments[account.Name].RemoveAll(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            deployments[account.Name].Add(deployment);
        }
    }

    public void RemoveDeployment(string accountName, string name)
    {
        lock (gate)
        {
            var account = FindAccount(accountName);
            var deployment = deployments[account.Name].FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
                throw new KeyNotFoundException("Unknown deployment.");
            deployments[account.Name].Remove(deployment);
            overrides.Remove((account.Name, deployment.Name));
        }
    }

    private AccountDefinition FindAccount(string name) => Scenario.Accounts.FirstOrDefault(account =>
        account.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException("Unknown account.");
}
