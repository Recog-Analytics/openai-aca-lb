namespace openai_loadbalancer.Operations;

public sealed record RequestAttemptRecord(string Deployment, string Account, string Region, int Tier,
    int? Status, double? TtfbMs, string HealthOutcome, string? RetryReason, string? DeploymentId = null)
{
    public string Outcome => HealthOutcome;
}

public sealed record RequestRecord(string Id, DateTimeOffset StartedAt, string? Caller, string? RequestedModel,
    string? ModelKey, string? Zone, bool Streaming, int Status, double DurationMs,
    IReadOnlyList<RequestAttemptRecord> Attempts, string Outcome, string? PoolKind = null, string? Pool = null);
