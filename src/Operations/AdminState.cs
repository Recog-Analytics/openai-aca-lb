using openai_loadbalancer.Discovery;
using openai_loadbalancer.Health;
using openai_loadbalancer.Pipeline;

namespace openai_loadbalancer.Operations;

public static class AdminState
{
    public static IEndpointConventionBuilder MapAdminState(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/admin/state", (HttpContext context, DiscoveryState discovery, IHealthState health) =>
        {
            var snapshot = discovery.Current;
            if (snapshot == null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (RequestInput.Authenticate(context.Request.Headers, snapshot.Callers) == null)
                return Results.Unauthorized();
            context.Response.Headers.CacheControl = "no-store";
            var states = health.GetSnapshot().ToDictionary(item => item.DeploymentId, StringComparer.OrdinalIgnoreCase);
            return Results.Ok(new
            {
                replica = Environment.MachineName,
                snapshot.RefreshedAt,
                deployments = snapshot.Table.Deployments.Select(deployment => new
                {
                    deployment.Id,
                    deployment.AccountId,
                    deployment.AccountName,
                    endpoint = deployment.Endpoint.AbsoluteUri,
                    deployment.DeploymentName,
                    model = deployment.Model.ToString(),
                    deployment.Region,
                    deployment.Sku,
                    deployment.Tier,
                    deployment.Zone,
                    deployment.Capacity,
                    deployment.Weight,
                    deployment.Disabled,
                    health = states.TryGetValue(deployment.Id, out var state) ? new
                    {
                        state = state.State.ToString(),
                        state.AccountOpen,
                        state.CanAttempt,
                        retryAfterSeconds = state.RetryAfter.TotalSeconds,
                        p95Seconds = state.P95?.TotalSeconds
                    } : null
                })
            });
        });
}
