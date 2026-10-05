using openai_loadbalancer.Configuration;
using openai_loadbalancer.Credentials;
using openai_loadbalancer.Health;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Discovery;

public static class DiscoveryRegistration
{
    public static IServiceCollection AddDiscovery(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DiscoveryOptions>().Bind(configuration.GetSection(DiscoveryOptions.SectionName))
            .Validate<IHostEnvironment>((options, environment) =>
                Uri.TryCreate(options.ArmEndpoint, UriKind.Absolute, out var endpoint) &&
                (endpoint.Scheme == Uri.UriSchemeHttps ||
                    endpoint.Scheme == Uri.UriSchemeHttp && environment.IsDevelopment()) &&
                endpoint.Host.Length > 0 && endpoint.UserInfo.Length == 0 &&
                endpoint.AbsolutePath == "/" && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0,
                "Discovery:ArmEndpoint must be an HTTPS origin; HTTP is allowed only in Development.")
            .Validate(options => options.RefreshInterval.TotalMilliseconds >= 1 &&
                options.RefreshInterval.TotalMilliseconds <= uint.MaxValue - 1,
                "Discovery:RefreshInterval must be at least one millisecond and fit the supported timer range.")
            .ValidateOnStart();
        services.AddAzureCredential(configuration);
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
