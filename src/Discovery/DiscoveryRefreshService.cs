using Microsoft.Extensions.Options;
using openai_loadbalancer.Configuration;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Discovery;

public sealed class DiscoveryRefreshService(
    IArmClient armClient,
    IRoutingTableBuilder tableBuilder,
    IYamlConfigurationParser parser,
    IOptions<DiscoveryOptions> options,
    IHostEnvironment environment,
    DiscoveryState state,
    TimeProvider timeProvider,
    ILogger<DiscoveryRefreshService> logger) : BackgroundService
{
    private readonly SemaphoreSlim refreshLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RefreshAsync(stoppingToken);
            using var timer = new PeriodicTimer(options.Value.RefreshInterval, timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RefreshAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            var config = options.Value;
            if (config.Scopes.Length == 0 || string.IsNullOrWhiteSpace(config.OverridesFilePath) ||
                string.IsNullOrWhiteSpace(config.CallersFilePath))
                throw new InvalidOperationException("Discovery requires scopes, OverridesFilePath and CallersFilePath.");
            var scopes = config.Scopes.Select(DiscoveryScope.Parse)
                .DistinctBy(scope => scope.ResourceId, StringComparer.OrdinalIgnoreCase).ToArray();

            var overrides = parser.ParseOverrides(await File.ReadAllTextAsync(FilePath(config.OverridesFilePath), cancellationToken));
            var callers = parser.ParseCallers(await File.ReadAllTextAsync(FilePath(config.CallersFilePath), cancellationToken));
            var locations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var subscription in scopes.Select(scope => scope.SubscriptionId).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var (region, geography) in await armClient.GetLocationsAsync(subscription, cancellationToken))
                    locations[region] = geography;

            var accounts = new Dictionary<string, ArmAccount>(StringComparer.OrdinalIgnoreCase);
            foreach (var scope in scopes)
                foreach (var account in await armClient.GetAccountsAsync(scope, cancellationToken))
                    accounts.TryAdd(account.Id, account);

            var deployments = new List<DiscoveredDeployment>();
            foreach (var account in accounts.Values)
                deployments.AddRange(await armClient.GetDeploymentsAsync(account, cancellationToken));

            var table = tableBuilder.Build(deployments, new RegionGeography(locations), overrides);
            var snapshot = new DiscoverySnapshot(table, callers, timeProvider.GetUtcNow());
            cancellationToken.ThrowIfCancellationRequested();
            var previous = state.Current;
            state.Publish(snapshot, overrides);
            LogChanges(previous?.Table, table);
            logger.LogInformation("Discovery refresh succeeded with {DeploymentCount} deployments.", table.Deployments.Count);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Discovery refresh failed; retaining the last successful snapshot. Ready: {Ready}.", state.IsReady);
            return false;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private string FilePath(string path) => Path.IsPathRooted(path) ? path : Path.Combine(environment.ContentRootPath, path);

    private void LogChanges(RoutingTable? previous, RoutingTable current)
    {
        var oldDeployments = previous?.Deployments.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, Deployment>(StringComparer.OrdinalIgnoreCase);
        var newDeployments = current.Deployments.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var deployment in current.Deployments)
        {
            if (!oldDeployments.TryGetValue(deployment.Id, out var old))
                logger.LogInformation("Added deployment {DeploymentId} with weight {Weight}.", deployment.Id, deployment.Weight);
            else if (old.Capacity != deployment.Capacity || old.Weight != deployment.Weight)
                logger.LogInformation("Deployment {DeploymentId} capacity changed from {OldCapacity} to {Capacity}; weight changed from {OldWeight} to {Weight}.",
                    deployment.Id, old.Capacity, deployment.Capacity, old.Weight, deployment.Weight);
        }
        foreach (var deployment in oldDeployments.Values)
            if (!newDeployments.ContainsKey(deployment.Id))
                logger.LogInformation("Removed deployment {DeploymentId}.", deployment.Id);
    }
}
