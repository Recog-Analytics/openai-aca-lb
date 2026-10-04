using openai_loadbalancer.Configuration;

namespace openai_loadbalancer.Tests;

public class YamlConfigurationParserTests
{
    private readonly YamlConfigurationParser parser = new();

    [Fact]
    public void ParsesCompleteOverrideExample()
    {
        var config = parser.ParseOverrides("""
            defaultVersions:
              gpt-4o: "2024-11-20"
            exclude:
              - account: oai-legacy-westeurope
              - account: oai-swedencentral
                deployment: gpt4o-test
            deployments:
              - account: oai-francecentral
                deployment: gpt4o
                tier: 1
                weightMultiplier: 0.5
                disabled: true
            regions:
              switzerlandnorth: { zone: eu }
            """);
        Assert.Equal("2024-11-20", config.DefaultVersions["gpt-4o"]);
        Assert.Equal(2, config.Exclude.Count);
        Assert.Null(config.Exclude[0].Deployment);
        Assert.Equal("gpt4o-test", config.Exclude[1].Deployment);
        var item = Assert.Single(config.Deployments);
        Assert.Equal("oai-francecentral", item.Account);
        Assert.Equal("gpt4o", item.Deployment);
        Assert.Equal(1, item.Tier);
        Assert.Equal(0.5m, item.WeightMultiplier);
        Assert.True(item.Disabled);
        Assert.Equal("eu", config.Regions["switzerlandnorth"].Zone);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# optional overrides")]
    [InlineData("{}")]
    public void EmptyOverridesUseDefaults(string yaml)
    {
        var config = parser.ParseOverrides(yaml);
        Assert.Empty(config.DefaultVersions);
        Assert.Empty(config.Exclude);
        Assert.Empty(config.Deployments);
        Assert.Empty(config.Regions);
    }

    [Fact]
    public void OmittedDeploymentSettingsKeepDefaults()
    {
        var item = Assert.Single(parser.ParseOverrides("deployments: [{ account: oai, deployment: gpt4o }]").Deployments);
        Assert.Null(item.Tier);
        Assert.Equal(1, item.WeightMultiplier);
        Assert.False(item.Disabled);
    }

    [Theory]
    [InlineData("unknown: true")]
    [InlineData("defaultVersions: { gpt-4o: one, gpt-4o: two }")]
    [InlineData("exclude: [{ deployment: test }]")]
    [InlineData("deployments: [{ account: oai }]")]
    [InlineData("regions: { westeurope: {} }")]
    [InlineData("exclude: null")]
    [InlineData("regions: null")]
    [InlineData("deployments: [{ account: '', deployment: test }]")]
    [InlineData("deployments: [{ account: oai, deployment: test, tier: 3 }]")]
    [InlineData("deployments: [{ account: oai, deployment: test, tier: -1 }]")]
    [InlineData("deployments: [{ account: oai, deployment: test, weightMultiplier: -0.5 }]")]
    [InlineData("deployments: [{ account: oai, deployment: test }, { account: OAI, deployment: TEST }]")]
    [InlineData("defaultVersions: { gpt-4o: '' }")]
    [InlineData("regions: { westeurope: { zone: '' } }")]
    [InlineData("deployments: [")]
    public void RejectsInvalidOverrides(string yaml)
    {
        Assert.ThrowsAny<Exception>(() => parser.ParseOverrides(yaml));
    }

    [Fact]
    public void ParsesCallerZoneOrderAndRotationHashes()
    {
        var first = "sha256:" + new string('a', 64);
        var second = "sha256:" + new string('b', 64);
        var config = parser.ParseCallers($"""
            callers:
              - name: orchestrator
                zones: [eu, global]
                keyHashes: ["{first}", "{second}"]
            """);
        var caller = Assert.Single(config.Callers);
        Assert.Equal("orchestrator", caller.Name);
        Assert.Equal(new[] { "eu", "global" }, caller.Zones);
        Assert.Equal(new[] { first, second }, caller.KeyHashes);
    }

    [Theory]
    [InlineData("name: ''", "[eu]", "HASH")]
    [InlineData("name: caller", "[]", "HASH")]
    [InlineData("name: caller", "['']", "HASH")]
    [InlineData("name: caller", "null", "HASH")]
    [InlineData("name: caller", "[eu]", "sha256:abcd")]
    [InlineData("name: caller", "[eu]", "lbk_plaintext")]
    [InlineData("name: caller", "[eu]", "sha256:gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void RejectsInvalidCallerFields(string name, string zones, string hash)
    {
        hash = hash == "HASH" ? "sha256:" + new string('a', 64) : hash;
        var yaml = $"callers: [{{ {name}, zones: {zones}, keyHashes: ['{hash}'] }}]";
        Assert.ThrowsAny<Exception>(() => parser.ParseCallers(yaml));
    }

    [Theory]
    [InlineData("callers: [{ name: caller, zones: [eu] }]")]
    [InlineData("callers: [{ zones: [eu], keyHashes: [] }]")]
    [InlineData("callers: [{ name: caller, keyHashes: [] }]")]
    [InlineData("callers: [{ name: caller, zones: [eu], keyHashes: [] }]")]
    [InlineData("callers: [{ name: caller, zones: [eu], keyHashes: null }]")]
    [InlineData("callers: null")]
    [InlineData("callers: [")]
    [InlineData("caller: []")]
    public void RejectsMissingOrInvalidCallerConfiguration(string yaml)
    {
        Assert.ThrowsAny<Exception>(() => parser.ParseCallers(yaml));
    }

    [Fact]
    public void RejectsDuplicateCallersAndAmbiguousKeyOwnership()
    {
        var hash = "sha256:" + new string('a', 64);
        var other = "sha256:" + new string('b', 64);
        Assert.Throws<ArgumentException>(() => parser.ParseCallers($$"""
            callers:
              - { name: same, zones: [eu], keyHashes: ['{{hash}}'] }
              - { name: same, zones: [us], keyHashes: ['{{other}}'] }
            """));
        Assert.Throws<ArgumentException>(() => parser.ParseCallers($$"""
            callers:
              - { name: first, zones: [eu], keyHashes: ['{{hash}}'] }
              - { name: second, zones: [us], keyHashes: ['{{hash}}'] }
            """));
    }

    [Fact]
    public void EmptyCallersFileHasNoCallers()
    {
        Assert.Empty(parser.ParseCallers("").Callers);
    }
}
