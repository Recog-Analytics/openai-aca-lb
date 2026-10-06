using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using openai_loadbalancer.Credentials;
using openai_loadbalancer.Discovery;
using openai_loadbalancer.Health;

namespace openai_loadbalancer.Dashboard;

public sealed class DashboardPublisherOptions
{
    public string? IngestUrl { get; set; }
    public string? Credential { get; set; }
    public string Audience { get; set; } = "";
}

public static class DashboardRegistration
{
    public static IServiceCollection AddDashboardPublisher(this IServiceCollection services, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["Dashboard:IngestUrl"]))
            return services;
        var mode = configuration["Dashboard:Credential"] ?? configuration["Azure:Credential"] ?? "ManagedIdentity";
        services.AddOptions<DashboardPublisherOptions>().Bind(configuration.GetSection("Dashboard"))
            .Validate<IHostEnvironment>((options, environment) =>
                Uri.TryCreate(options.IngestUrl, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 &&
                (uri.Scheme == "https" || uri.Scheme == "http" && environment.IsDevelopment()),
                "Dashboard:IngestUrl must be HTTPS; HTTP is allowed only in Development.")
            .Validate(_ => mode is "ManagedIdentity" or "Fake", "Dashboard:Credential must be ManagedIdentity or Fake.")
            .Validate<IHostEnvironment>((_, environment) => mode != "Fake" || environment.IsDevelopment(),
                "Dashboard:Credential Fake is allowed only in Development.")
            .Validate(options => mode == "Fake" || !string.IsNullOrWhiteSpace(options.Audience),
                "Dashboard:Audience is required for managed identity.").ValidateOnStart();
        services.AddSingleton<DashboardEventBuffer>();
        services.AddHttpClient("dashboard", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddLogging(logging => logging.AddFilter("System.Net.Http.HttpClient.dashboard", LogLevel.None));
        services.AddHostedService(provider =>
        {
            TokenCredential credential;
            if (mode == (configuration["Azure:Credential"] ?? "ManagedIdentity"))
                credential = provider.GetRequiredService<TokenCredential>();
            else if (mode == "Fake")
                credential = new FakeTokenCredential();
            else
            {
                var clientId = configuration["AZURE_CLIENT_ID"];
                credential = new ManagedIdentityCredential(string.IsNullOrWhiteSpace(clientId)
                    ? ManagedIdentityId.SystemAssigned : ManagedIdentityId.FromUserAssignedClientId(clientId));
            }
            return new DashboardPublisher(provider.GetRequiredService<DashboardEventBuffer>(),
                provider.GetRequiredService<DiscoveryState>(), provider.GetRequiredService<IHealthState>(),
                provider.GetRequiredService<IHttpClientFactory>(), credential,
                provider.GetRequiredService<IOptions<DashboardPublisherOptions>>(), provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<DashboardPublisher>>());
        });
        return services;
    }
}
