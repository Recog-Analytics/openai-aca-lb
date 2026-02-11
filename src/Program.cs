using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Transforms;

namespace openai_loadbalancer;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var backendConfiguration = BackendConfig.LoadConfig(builder.Configuration);
        var latencyConfig = LatencyConfig.LoadFromEnvironment();
        var yarpConfiguration = new YarpConfiguration(backendConfiguration);
        builder.Services.AddSingleton<IPassiveHealthCheckPolicy, ThrottlingHealthPolicy>();
        builder.Services.AddReverseProxy().AddTransforms(m =>
        {
            m.AddRequestTransform(yarpConfiguration.TransformRequest());
            m.AddResponseTransform(yarpConfiguration.TransformResponse());
        }).LoadFromMemory(yarpConfiguration.GetRoutes(), yarpConfiguration.GetClusters());

        builder.Services.AddHealthChecks();

        var app = builder.Build();
        var latencyTracker = new LatencyTracker(latencyConfig, app.Services.GetRequiredService<ILoggerFactory>());

        app.MapHealthChecks("/healthz");
        app.MapReverseProxy(m =>
        {
            m.UseMiddleware<RetryMiddleware>(backendConfiguration, latencyTracker, latencyConfig);
            m.UsePassiveHealthChecks();
        });

        app.Run();
    }
}
