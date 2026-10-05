using openai_loadbalancer.Discovery;
using openai_loadbalancer.Pipeline;
using openai_loadbalancer.Operations;
using openai_loadbalancer.Dashboard;

namespace openai_loadbalancer;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddOperations(builder.Configuration);
        builder.Services.AddDiscovery(builder.Configuration);
        builder.Services.AddDashboardPublisher(builder.Configuration);

        builder.Services.AddRequestPipeline(builder.Configuration);
        builder.Services.AddHealthChecks();

        var app = builder.Build();
        app.MapHealthChecks("/healthz");
        app.MapDiscoveryReadiness();
        app.MapAdminState();
        app.MapAdminRequests();
        app.MapRequestPipeline();

        app.Run();
    }
}
