using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Discovery;

public sealed record ArmAccount(string Id, string Name, Uri Endpoint, string Region);

public interface IArmClient
{
    Task<IReadOnlyList<ArmAccount>> GetAccountsAsync(DiscoveryScope scope, CancellationToken cancellationToken);
    Task<IReadOnlyList<DiscoveredDeployment>> GetDeploymentsAsync(ArmAccount account, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, string>> GetLocationsAsync(string subscriptionId, CancellationToken cancellationToken);
}
