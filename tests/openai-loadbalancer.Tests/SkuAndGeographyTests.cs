using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Tests;

public class SkuAndGeographyTests
{
    [Theory]
    [InlineData("ProvisionedManaged", 0, false)]
    [InlineData("DataZoneProvisionedManaged", 0, false)]
    [InlineData("GlobalProvisionedManaged", 0, true)]
    [InlineData("Standard", 1, false)]
    [InlineData("DataZoneStandard", 1, false)]
    [InlineData("GlobalStandard", 2, true)]
    public void MapsSupportedSkus(string sku, int tier, bool global)
    {
        Assert.Equal(new SkuMapping(tier, global), SkuMapping.FromSku(sku));
    }

    [Theory]
    [InlineData("Batch")]
    [InlineData("GlobalBatch")]
    [InlineData("DataZoneBatch")]
    [InlineData("FutureSku")]
    [InlineData("")]
    public void ExcludesBatchAndUnknownSkus(string sku)
    {
        Assert.Null(SkuMapping.FromSku(sku));
    }

    [Theory]
    [InlineData("Europe", "eu")]
    [InlineData("US", "us")]
    [InlineData("United States", "us")]
    [InlineData("Canada", "canada")]
    [InlineData("Asia Pacific", "asia-pacific")]
    [InlineData(" eu ", "eu")]
    public void MapsGeographyInput(string geography, string expected)
    {
        var mapping = new RegionGeography(new Dictionary<string, string> { ["region"] = geography });
        Assert.Equal(expected, mapping.GetZone("REGION"));
    }

    [Fact]
    public void MissingGeographyFailsInsteadOfAllowingGlobalRouting()
    {
        var mapping = new RegionGeography(new Dictionary<string, string>());
        Assert.Throws<ArgumentException>(() => mapping.GetZone("unknown"));
    }
}
