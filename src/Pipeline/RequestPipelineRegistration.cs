using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using openai_loadbalancer.Operations;
using Yarp.ReverseProxy.Forwarder;

namespace openai_loadbalancer.Pipeline;

public sealed class RequestPipelineOptions
{
    public static readonly TimeSpan MaximumOverallTimeout = TimeSpan.FromSeconds(600);

    // Non-streaming headers arrive after the whole completion, so only the overall deadline applies by default.
    public TimeSpan? NonStreamingTtfbTimeout { get; set; }
    public const int MaximumBodyBytesLimit = 1024 * 1024 * 1024;

    // Covers 25 MB audio uploads with multipart overhead.
    public int MaximumBodyBytes { get; set; } = RequestInput.DefaultMaximumBodyBytes;
    public TimeSpan StreamingTtfbTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan OverallTimeout { get; set; } = TimeSpan.FromSeconds(120);
}

public static class RequestPipelineRegistration
{
    public static IServiceCollection AddRequestPipeline(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RequestPipelineOptions>().Bind(configuration.GetSection("RequestPipeline"))
            .Validate(options => options.NonStreamingTtfbTimeout is null || options.NonStreamingTtfbTimeout > TimeSpan.Zero,
                "NonStreamingTtfbTimeout must be positive when set.")
            .Validate(options => options.StreamingTtfbTimeout > TimeSpan.Zero && options.OverallTimeout > TimeSpan.Zero &&
                options.OverallTimeout <= RequestPipelineOptions.MaximumOverallTimeout,
                "Timeouts must be positive; overall timeout cannot exceed 600 seconds.")
            .Validate(options => options.MaximumBodyBytes is > 0 and <= RequestPipelineOptions.MaximumBodyBytesLimit,
                "MaximumBodyBytes must be between 1 byte and 1 GiB.")
            .ValidateOnStart();
        services.TryAddSingleton<OperationsTelemetry>();
        services.TryAddSingleton<RequestHistory>();
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
