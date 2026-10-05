namespace openai_loadbalancer.Operations;

public sealed record RequestAttemptRecord(string Deployment, string Account, string Region, int Tier,
    int? Status, double? TtfbMs, string HealthOutcome, string? RetryReason, string? DeploymentId = null,
    string? ErrorCode = null, string? ErrorMessage = null, string? BackendRequestId = null)
{
    public string Outcome => HealthOutcome;
}

public sealed record RequestRecord(string Id, DateTimeOffset StartedAt, string? Caller, string? RequestedModel,
    string? ModelKey, string? Zone, bool Streaming, int Status, double DurationMs,
    IReadOnlyList<RequestAttemptRecord> Attempts, string Outcome, string? PoolKind = null, string? Pool = null,
    string? Operation = null, string? ApiVersion = null, long? RequestBytes = null, int? MaxOutputTokens = null);
