using System.Diagnostics;
using System.Diagnostics.Metrics;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Operations;

public sealed class OperationsTelemetry : IHealthObserver, IDisposable
{
    public const string MeterName = "openai_loadbalancer";
    private readonly Meter meter = new(MeterName);
    private readonly ILogger<OperationsTelemetry> logger;
    private readonly ISlackAlerts? alerts;
    private readonly Counter<long> requests;
    private readonly Counter<long> retries;
    private readonly Counter<long> transitions;
    private readonly Histogram<double> ttfb;
    private readonly Histogram<double> throttle;

    public OperationsTelemetry(ILogger<OperationsTelemetry> logger, ISlackAlerts? alerts = null)
    {
        this.logger = logger;
        this.alerts = alerts;
        requests = meter.CreateCounter<long>("lb.requests", "{request}");
        retries = meter.CreateCounter<long>("lb.retries", "{retry}");
        transitions = meter.CreateCounter<long>("lb.state_transitions", "{transition}");
        ttfb = meter.CreateHistogram<double>("lb.ttfb", "s");
        throttle = meter.CreateHistogram<double>("lb.throttle_time", "s");
    }

    public void RecordRequest(string? caller, ModelKey? model, Deployment? deployment, int status, string outcome)
    {
        var tags = Tags(caller, model, deployment);
        tags.Add("outcome", outcome);
        tags.Add("status", status);
        requests.Add(1, tags);
        logger.LogInformation("Request completed for caller {Caller}, model {Model}, deployment {Deployment}, region {Region}, tier {Tier}: {Status}, {Outcome}",
            caller ?? "unknown", model?.ToString() ?? "unknown", deployment?.Id ?? "none", deployment?.Region ?? "none", deployment?.Tier, status, outcome);
    }

    public void RecordRetry(string caller, Deployment deployment) =>
        retries.Add(1, Tags(caller, deployment.Model, deployment));

    public void RecordTtfb(string caller, Deployment deployment, TimeSpan duration) =>
        ttfb.Record(duration.TotalSeconds, Tags(caller, deployment.Model, deployment));

    public void RecordThrottle(string caller, Deployment deployment, TimeSpan duration) =>
        throttle.Record(duration.TotalSeconds, Tags(caller, deployment.Model, deployment));

    public void OnTransition(HealthTransition transition)
    {
        var tags = Tags(null, transition.Deployment.Model, transition.Deployment);
        tags.Add("previous", transition.Previous.ToString());
        tags.Add("state", transition.Current.ToString());
        tags.Add("misconfigured", transition.Misconfigured);
        transitions.Add(1, tags);
        logger.LogInformation("Deployment {Deployment} model {Model} region {Region} tier {Tier} changed from {Previous} to {State}; misconfigured: {Misconfigured}",
            transition.Deployment.Id, transition.Deployment.Model.ToString(), transition.Deployment.Region, transition.Deployment.Tier,
            transition.Previous, transition.Current, transition.Misconfigured);
        alerts?.Enqueue(transition);
    }

    private static TagList Tags(string? caller, ModelKey? model, Deployment? deployment) => new()
    {
        { "caller", caller ?? "unknown" },
        { "model", model?.ToString() ?? "unknown" },
        { "deployment", deployment?.Id ?? "none" },
        { "region", deployment?.Region ?? "none" },
        { "tier", deployment?.Tier ?? -1 }
    };

    public void Dispose() => meter.Dispose();
}
