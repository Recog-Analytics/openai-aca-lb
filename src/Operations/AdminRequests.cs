using System.Globalization;
using openai_loadbalancer.Discovery;
using openai_loadbalancer.Pipeline;

namespace openai_loadbalancer.Operations;

public static class AdminRequests
{
    public static IEndpointConventionBuilder MapAdminRequests(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/admin/requests", (HttpContext context, DiscoveryState discovery, RequestHistory history) =>
        {
            var snapshot = discovery.Current;
            if (snapshot == null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (RequestInput.Authenticate(context.Request.Headers, snapshot.Callers) == null)
                return Results.Unauthorized();
            context.Response.Headers.CacheControl = "no-store";
            var limit = 100;
            if (context.Request.Query.TryGetValue("limit", out var limits) &&
                (limits.Count != 1 || !int.TryParse(limits[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit <= 0))
                return Results.BadRequest();
            return Results.Ok(history.GetRecent(Math.Min(limit, RequestHistory.Capacity)));
        });
}
