using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using openai_loadbalancer.Operations;
using Yarp.ReverseProxy.Forwarder;

namespace openai_loadbalancer.Pipeline;

public sealed class RequestPipelineOptions
{
    public TimeSpan NonStreamingTtfbTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan StreamingTtfbTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan OverallTimeout { get; set; } = TimeSpan.FromSeconds(120);
}

public static class RequestPipelineRegistration
{
    public static IServiceCollection AddRequestPipeline(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RequestPipelineOptions>().Bind(configuration.GetSection("RequestPipeline"))
            .Validate(options => options.NonStreamingTtfbTimeout > TimeSpan.Zero && options.StreamingTtfbTimeout > TimeSpan.Zero &&
                options.OverallTimeout > TimeSpan.Zero && options.OverallTimeout <= TimeSpan.FromSeconds(120), "Timeouts must be positive; overall timeout cannot exceed 120 seconds.")
            .ValidateOnStart();
        services.TryAddSingleton<OperationsTelemetry>();
        services.AddHttpForwarder();
        services.AddSingleton<IBackendTokenProvider, BackendTokenProvider>();
        services.AddSingleton<ISelectionRandom, SelectionRandom>();
        services.AddSingleton<IDeploymentSelector, DeploymentSelector>();
        services.AddSingleton(_ => new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = Timeout.InfiniteTimeSpan
        }));
        services.AddSingleton<RequestPipeline>();
        return services;
    }

    public static IEndpointConventionBuilder MapRequestPipeline(this IEndpointRouteBuilder endpoints) =>
        endpoints.Map("/{**path}", (HttpContext context, RequestPipeline pipeline) => pipeline.InvokeAsync(context));
}
