using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace openai_loadbalancer.Operations;

public sealed class OperationsOptions
{
    public string? SlackWebhookUrl { get; set; }
}

public static class OperationsRegistration
{
    public static IServiceCollection AddOperations(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OperationsOptions>().Bind(configuration.GetSection("Operations"))
            .Validate(options => string.IsNullOrWhiteSpace(options.SlackWebhookUrl) ||
                (Uri.TryCreate(options.SlackWebhookUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)),
                "Slack webhook must be an HTTPS URL.").ValidateOnStart();
        services.AddHttpClient("slack", client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        // The webhook path contains a secret. Suppress HttpClient's automatic URL logs.
        services.AddLogging(logging => logging.AddFilter("System.Net.Http.HttpClient.slack", LogLevel.None));
        services.AddSingleton<SlackAlerts>();
        services.AddSingleton<ISlackAlerts>(provider => provider.GetRequiredService<SlackAlerts>());
        services.AddHostedService(provider => provider.GetRequiredService<SlackAlerts>());
        services.TryAddSingleton<OperationsTelemetry>();
        services.TryAddSingleton<RequestHistory>();
        services.AddSingleton<IHealthObserver>(provider => provider.GetRequiredService<OperationsTelemetry>());

        var connectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        var telemetry = services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService("openai-loadbalancer"))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(OperationsTelemetry.MeterName, Dashboard.DashboardEventBuffer.MeterName);
                if (!string.IsNullOrWhiteSpace(connectionString))
                    metrics.AddAzureMonitorMetricExporter(options => options.ConnectionString = connectionString);
            });
        telemetry.WithLogging(logging =>
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
                logging.AddAzureMonitorLogExporter(options => options.ConnectionString = connectionString);
        }, options => options.IncludeFormattedMessage = true);
        return services;
    }
}
