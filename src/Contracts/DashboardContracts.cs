using openai_loadbalancer.Operations;

namespace openai_loadbalancer.Dashboard;

public sealed record DashboardDeployment(string Id, string Account, string Deployment, string Region,
    string ModelKey, int Tier, string Zone, decimal Weight, string State, double? P95TtfbMs, bool AccountOpen,
    DateTimeOffset? ThrottledUntil = null, DateTimeOffset? OpenUntil = null, bool HalfOpen = false);

public sealed record DashboardState(DateTimeOffset? RefreshedAt, IReadOnlyList<DashboardDeployment> Deployments);
public sealed record DashboardCount(string DeploymentId, string Outcome, long Count);

/// <summary>One backend attempt of a route: the deployment tried and its health outcome.</summary>
public sealed record DashboardHop(string DeploymentId, string Outcome);

/// <summary>
/// Unsampled count of completed requests that share caller, model, zone, final status and attempt chain.
/// The dashboard derives every traffic share (caller, tier, region, deployment, fallback) from these.
/// </summary>
public sealed record DashboardRoute(string? Caller, string? ModelKey, string? Zone, int Status,
    IReadOnlyList<DashboardHop> Hops, long Count, string? PoolKind = null, string? Pool = null)
{
    public static DashboardRoute From(RequestRecord request) => new(request.Caller, request.ModelKey, request.Zone,
        request.Status, request.Attempts.Select(attempt => new DashboardHop(attempt.DeploymentId ?? attempt.Deployment, attempt.Outcome)).ToArray(), 1,
        request.PoolKind, request.Pool);

    /// <summary>Identity of a route without its count, so equal routes from any replica add up.</summary>
    public static string KeyOf(DashboardRoute route) => string.Join('\u001f',
        [route.Caller ?? "", route.ModelKey ?? "", route.PoolKind ?? "", route.Pool ?? "", route.Zone ?? "", route.Status.ToString(System.Globalization.CultureInfo.InvariantCulture),
         .. route.Hops.Select(hop => hop.DeploymentId + '\u001e' + hop.Outcome)]);
}

public sealed record DashboardBatch(string Replica, DateTimeOffset SentAt, DashboardState State,
    IReadOnlyList<RequestRecord> Requests, IReadOnlyList<DashboardCount> Counts, IReadOnlyList<DashboardRoute>? Routes = null);
