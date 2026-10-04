using Azure.Core;
using Azure.Identity;
using openai_loadbalancer.Configuration;
using openai_loadbalancer.Health;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Discovery;

public static class DiscoveryRegistration
{
    public static IServiceCollection AddDiscovery(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DiscoveryOptions>(configuration.GetSection(DiscoveryOptions.SectionName));
        services.AddSingleton<TokenCredential>(_ =>
        {
            var clientId = configuration["AZURE_CLIENT_ID"];
            var identity = string.IsNullOrWhiteSpace(clientId) ? ManagedIdentityId.SystemAssigned
                : ManagedIdentityId.FromUserAssignedClientId(clientId);
            return new ManagedIdentityCredential(identity);
        });
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IHealthState, HealthState>();
        services.AddSingleton<IRetryBudget, RetryBudget>();
        services.AddHttpClient<IArmClient, ArmClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IYamlConfigurationParser, YamlConfigurationParser>();
        services.AddSingleton<IRoutingTableBuilder, RoutingTableBuilder>();
        services.AddSingleton<DiscoveryState>();
        services.AddHostedService<DiscoveryRefreshService>();
        return services;
    }

    public static IEndpointConventionBuilder MapDiscoveryReadiness(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/readyz", (DiscoveryState state) => state.IsReady
            ? Results.Ok("Ready") : Results.StatusCode(StatusCodes.Status503ServiceUnavailable)).AllowAnonymous();
}
