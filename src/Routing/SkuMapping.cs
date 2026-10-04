namespace openai_loadbalancer.Routing;

public readonly record struct SkuMapping(int Tier, bool Global)
{
    public static SkuMapping? FromSku(string sku) => sku switch
    {
        "ProvisionedManaged" or "DataZoneProvisionedManaged" => new(0, false),
        "GlobalProvisionedManaged" => new(0, true),
        "Standard" or "DataZoneStandard" => new(1, false),
        "GlobalStandard" => new(2, true),
        _ => null
    };
}
