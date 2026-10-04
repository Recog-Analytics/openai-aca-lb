using openai_loadbalancer.Health;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Operations;

public sealed record HealthTransition(Deployment Deployment, DeploymentHealth Previous,
    DeploymentHealth Current, bool Misconfigured, TimeSpan RetryAfter);

public interface IHealthObserver
{
    void OnTransition(HealthTransition transition);
}
