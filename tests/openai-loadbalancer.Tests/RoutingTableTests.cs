using openai_loadbalancer.Configuration;
using openai_loadbalancer.Routing;
using Microsoft.Extensions.Logging;

namespace openai_loadbalancer.Tests;

public class RoutingTableTests
{
    private static readonly RegionGeography Geography = new(new Dictionary<string, string>
    {
        ["westeurope"] = "Europe",
        ["eastus"] = "US"
    });

    private static DiscoveredDeployment Discover(string name = "gpt4o", string version = "2024-11-20",
        string account = "oai-eu", string sku = "Standard", decimal capacity = 100,
        string region = "westeurope", string state = "Succeeded", string model = "gpt-4o") =>
        new($"/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/{account}",
            account, new Uri($"https://{account}.openai.azure.com"), region, name,
            new ModelKey(model, version), sku, capacity, state);

    private static RoutingTable Build(RoutingOverrides? overrides = null, params DiscoveredDeployment[] deployments) =>
        new RoutingTableBuilder().Build(deployments, Geography, overrides ?? new RoutingOverrides());

    [Fact]
    public void BuildsRecordsAndGroupsByExactModelKey()
    {
        var input = Discover();
        var table = Build(null, input, Discover("second"), Discover("older", "2024-08-06"), Discover("other", model: "o3"));
        var deployment = table.Deployments[0];
        Assert.Equal(input.AccountId + "/deployments/gpt4o", deployment.Id);
        Assert.Equal(input.AccountName, deployment.AccountName);
        Assert.Equal(input.Endpoint, deployment.Endpoint);
        Assert.Equal(input.Region, deployment.Region);
        Assert.Equal(input.DeploymentName, deployment.DeploymentName);
        Assert.Equal(input.Model, deployment.Model);
        Assert.Equal(input.Sku, deployment.Sku);
        Assert.Equal(1, deployment.Tier);
        Assert.Equal("eu", deployment.Zone);
        Assert.Equal(100, deployment.Weight);
        Assert.False(deployment.Disabled);
        Assert.Equal(2, table.GetDeployments(input.Model).Count);
        Assert.Empty(table.GetDeployments(new ModelKey("missing", "1")));
    }

    [Fact]
    public void ExcludesUnsuccessfulBatchAndUnknownDeploymentsBeforeOverrides()
    {
        var overrides = new RoutingOverrides
        {
            Deployments = [new() { Account = "oai-eu", Deployment = "batch", Tier = 1 }]
        };
        var table = Build(overrides, Discover(), Discover("creating", state: "Creating"),
            Discover("failed", state: "Failed"), Discover("batch", sku: "GlobalBatch"), Discover("unknown", sku: "Future"));
        Assert.Single(table.Deployments);
    }

    [Fact]
    public void ExclusionWinsAndAccountExclusionRemovesAllItsDeployments()
    {
        var overrides = new RoutingOverrides
        {
            Exclude = [new() { Account = "OAI-EU" }, new() { Account = "oai-us", Deployment = "TEST" }],
            Deployments = [new() { Account = "oai-eu", Deployment = "gpt4o", Disabled = false }]
        };
        var table = Build(overrides, Discover(), Discover("second"), Discover("test", account: "oai-us"),
            Discover("production", account: "oai-us"));
        Assert.Equal("production", Assert.Single(table.Deployments).DeploymentName);
    }

    [Fact]
    public void AppliesTierWeightDrainAndRegionOverridesWithoutChangingInput()
    {
        var source = Discover();
        var overrides = new RoutingOverrides
        {
            Deployments = [new() { Account = "OAI-EU", Deployment = "GPT4O", Tier = 0, WeightMultiplier = 0.5m, Disabled = true }],
            Regions = new() { ["WESTEUROPE"] = new() { Zone = "custom" } }
        };
        var table = Build(overrides, source);
        var deployment = Assert.Single(table.Deployments);
        Assert.Equal(0, deployment.Tier);
        Assert.Equal(50, deployment.Weight);
        Assert.Equal("custom", deployment.Zone);
        Assert.True(deployment.Disabled);
        Assert.Equal(100, source.Capacity);
        Assert.True(table.ResolveModel("gpt-4o").Success);
    }

    [Theory]
    [InlineData("GlobalStandard")]
    [InlineData("GlobalProvisionedManaged")]
    public void TierAndRegionOverridesCannotMakeGlobalSkuRegional(string sku)
    {
        var overrides = new RoutingOverrides
        {
            Deployments = [new() { Account = "oai-eu", Deployment = "gpt4o", Tier = 1 }],
            Regions = new() { ["unknown"] = new() { Zone = "eu" } }
        };
        Assert.Equal("global", Assert.Single(Build(overrides, Discover(sku: sku, region: "unknown")).Deployments).Zone);
    }

    [Fact]
    public void UnknownGeographyIsExcludedWithWarningWithoutLosingKnownDeployments()
    {
        var logger = new TestLogger<RoutingTableBuilder>();
        var table = new RoutingTableBuilder(logger).Build([Discover(), Discover("unknown-deployment", region: "unknown")],
            Geography, new RoutingOverrides());
        Assert.Equal("gpt4o", Assert.Single(table.Deployments).DeploymentName);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("unknown-deployment", warning.Message);
        Assert.Contains("unknown", warning.Message);
        Assert.Contains("no known geography", warning.Message);
    }

    [Fact]
    public void RegionOverrideSuppliesMissingGeography()
    {
        var overrides = new RoutingOverrides { Regions = new() { ["unknown"] = new() { Zone = "eu" } } };
        Assert.Equal("eu", Assert.Single(Build(overrides, Discover(region: "unknown")).Deployments).Zone);
    }

    [Theory]
    [InlineData("Standard")]
    [InlineData("DataZoneStandard")]
    [InlineData("ProvisionedManaged")]
    [InlineData("DataZoneProvisionedManaged")]
    [InlineData("GlobalStandard")]
    [InlineData("GlobalProvisionedManaged")]
    public void UnknownGeographyExclusionAppliesToEverySku(string sku)
    {
        Assert.Empty(Build(null, Discover(sku: sku, region: "unknown")).Deployments);
    }

    [Fact]
    public void UsesEachRegionsGeography()
    {
        var table = Build(null, Discover(), Discover(account: "oai-us", region: "eastus"));
        Assert.Equal(new[] { "eu", "us" }, table.Deployments.Select(deployment => deployment.Zone));
    }

    [Fact]
    public void DoesNotNormalizeWeightsAcrossTiers()
    {
        var table = Build(null, Discover(sku: "ProvisionedManaged", capacity: 10), Discover("standard", capacity: 200));
        Assert.Equal(new decimal[] { 10, 200 }, table.Deployments.Select(deployment => deployment.Weight));
    }

    [Fact]
    public void KeepsZeroWeightAndRejectsNegativeCapacity()
    {
        Assert.Equal(0, Assert.Single(Build(null, Discover(capacity: 0)).Deployments).Weight);
        Assert.Throws<ArgumentException>(() => Build(null, Discover(capacity: -1)));
    }

    [Fact]
    public void RejectsDuplicateIdentityButDistinguishesAccountsInDifferentScopes()
    {
        var source = Discover();
        Assert.Throws<ArgumentException>(() => Build(null, source, source));
        var otherScope = source with { AccountId = source.AccountId.Replace("/sub/", "/other/") };
        var table = Build(null, source, otherScope);
        Assert.Equal(2, table.Deployments.Select(deployment => deployment.Id).Distinct().Count());
    }

    [Fact]
    public void SnapshotDoesNotChangeWhenInputsChange()
    {
        var overrides = new RoutingOverrides { DefaultVersions = new() { ["gpt-4o"] = "2024-11-20" } };
        var inputs = new List<DiscoveredDeployment> { Discover(), Discover("older", "2024-08-06") };
        var table = new RoutingTableBuilder().Build(inputs, Geography, overrides);
        inputs.Clear();
        overrides.DefaultVersions["gpt-4o"] = "2024-08-06";
        Assert.Equal(2, table.Deployments.Count);
        Assert.Equal("2024-11-20", table.ResolveModel("gpt-4o").Key?.Version);
        Assert.Throws<NotSupportedException>(() => ((IList<Deployment>)table.Deployments).Clear());
    }

    [Fact]
    public void ResolvesExplicitVersionEvenWhenDefaultDiffers()
    {
        var overrides = new RoutingOverrides { DefaultVersions = new() { ["gpt-4o"] = "2024-08-06" } };
        var table = Build(overrides, Discover(), Discover("older", "2024-08-06"));
        Assert.Equal(new ModelKey("gpt-4o", "2024-11-20"), table.ResolveModel("gpt-4o@2024-11-20").Key);
        Assert.Equal(new ModelKey("gpt-4o", "2024-08-06"), table.ResolveModel("gpt-4o").Key);
    }

    [Fact]
    public void ResolvesSingleVersionAcrossMultipleDeployments()
    {
        Assert.Equal(new ModelKey("gpt-4o", "2024-11-20"), Build(null, Discover(), Discover("second")).ResolveModel("gpt-4o").Key);
    }

    [Fact]
    public void AmbiguousModelListsVersionsInError()
    {
        var result = Build(null, Discover(), Discover("older", "2024-08-06")).ResolveModel("gpt-4o");
        Assert.False(result.Success);
        Assert.Equal(new[] { "2024-08-06", "2024-11-20" }, result.AvailableVersions);
        Assert.Contains("2024-08-06, 2024-11-20", result.Error);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("GPT-4O")]
    [InlineData("gpt-4o@missing")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("@version")]
    [InlineData("gpt-4o@")]
    [InlineData("gpt-4o@version@extra")]
    public void ReturnsResolutionErrorForUnknownOrMalformedModel(string requested)
    {
        var result = Build(null, Discover()).ResolveModel(requested);
        Assert.False(result.Success);
        Assert.Null(result.Key);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void MissingDefaultVersionDoesNotFallBackToOnlyAvailableVersion()
    {
        var overrides = new RoutingOverrides { DefaultVersions = new() { ["gpt-4o"] = "missing" } };
        var result = Build(overrides, Discover()).ResolveModel("gpt-4o");
        Assert.False(result.Success);
        Assert.Contains("missing", result.Error);
        Assert.Equal(new[] { "2024-11-20" }, result.AvailableVersions);
    }

    [Fact]
    public void EmptyTableReturnsUnknownModel()
    {
        Assert.Empty(Build().Deployments);
        Assert.False(Build().ResolveModel("gpt-4o").Success);
    }

    [Theory]
    [InlineData("chat", "2024-11-20")]
    [InlineData("CHAT", "2024-11-20")]
    [InlineData("latest", "2024-08-06")]
    public void ResolvesAliasesCaseInsensitivelyBeforeModelNames(string requested, string version)
    {
        var overrides = new RoutingOverrides
        {
            DefaultVersions = new() { ["gpt-4o"] = "2024-08-06" },
            Aliases = new() { ["chat"] = "gpt-4o@2024-11-20", ["latest"] = "gpt-4o" }
        };
        var table = Build(overrides, Discover(), Discover("older", "2024-08-06"));
        Assert.Equal(new ModelKey("gpt-4o", version), table.ResolveModel(requested).Key);
    }

    [Fact]
    public void AliasToUnavailableModelReturnsResolutionError()
    {
        var overrides = new RoutingOverrides { Aliases = new() { ["chat"] = "gpt-4o@missing" } };
        Assert.False(Build(overrides, Discover()).ResolveModel("chat").Success);
    }

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("GPT-4O")]
    public void AliasEqualToDiscoveredModelNameFailsBuild(string alias)
    {
        var overrides = new RoutingOverrides { Aliases = new() { [alias] = "o3@1" } };
        var error = Assert.Throws<ArgumentException>(() => Build(overrides, Discover(), Discover("o3", "1", model: "o3")));
        Assert.Contains(alias, error.Message);
    }

    [Fact]
    public void DeploymentNameResolvesToPoolOfThatNameOnly()
    {
        var table = Build(null, Discover("llm-gpt-4omini", account: "west", model: "gpt-4o-mini"),
            Discover("LLM-GPT-4OMINI", account: "france", model: "gpt-4o-mini"),
            Discover("llm-gpt-4omini-public", account: "west", model: "gpt-4o-mini"));
        var result = table.ResolveModel("llm-gpt-4omini");
        Assert.Equal(PoolKind.Deployment, result.Pool?.Kind);
        Assert.Equal(new ModelKey("gpt-4o-mini", "2024-11-20"), result.Key);
        Assert.Equal(new[] { "west", "france" }, table.GetDeployments(result.Pool!.Value).Select(item => item.AccountName));
        Assert.Equal("llm-gpt-4omini-public",
            Assert.Single(table.GetDeployments(table.ResolveModel("llm-gpt-4omini-public").Pool!.Value)).DeploymentName);
    }

    [Fact]
    public void ResolutionOrderIsModelKeyThenAliasThenDeploymentName()
    {
        var overrides = new RoutingOverrides { Aliases = new() { ["legacy"] = "o3-pool", ["mini"] = "gpt-4o" } };
        var table = Build(overrides, Discover("gpt-4o", account: "named-like-model", model: "o3"),
            Discover("legacy", account: "a"), Discover("o3-pool", account: "b", model: "o3"), Discover("mini", account: "c"));
        Assert.Equal(PoolKind.Model, table.ResolveModel("gpt-4o").Pool?.Kind);
        Assert.Equal(new RoutingPool(PoolKind.Deployment, "o3-pool"), table.ResolveModel("legacy").Pool);
        Assert.Equal(RoutingPool.For(new ModelKey("gpt-4o", "2024-11-20")), table.ResolveModel("MINI").Pool);
        Assert.False(table.ResolveModel("missing").Success);
    }

    [Fact]
    public void MixedModelDeploymentNameRoutesToPoolAndWarns()
    {
        var logger = new TestLogger<RoutingTableBuilder>();
        var table = new RoutingTableBuilder(logger).Build([Discover("shared", account: "a"),
            Discover("shared", "2024-08-06", account: "b")], Geography, new RoutingOverrides());
        var result = table.ResolveModel("shared");
        Assert.Null(result.Key);
        Assert.Equal(2, table.GetDeployments(result.Pool!.Value).Count);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("shared") &&
            entry.Message.Contains("gpt-4o@2024-08-06, gpt-4o@2024-11-20"));
    }
}
