using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using openai_loadbalancer.Health;

namespace openai_loadbalancer.Operations;

public interface ISlackAlerts
{
    void Enqueue(HealthTransition transition);
}

public sealed class SlackAlerts(IHttpClientFactory clients, IOptions<OperationsOptions> options,
    ILogger<SlackAlerts> logger) : BackgroundService, ISlackAlerts
{
    private readonly Channel<HealthTransition> queue = Channel.CreateBounded<HealthTransition>(new BoundedChannelOptions(100)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });

    public void Enqueue(HealthTransition transition)
    {
        if (string.IsNullOrWhiteSpace(options.Value.SlackWebhookUrl) || !ShouldAlert(transition))
            return;
        if (!queue.Writer.TryWrite(transition))
            logger.LogWarning("Slack alert queue is full; dropped a health alert");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var transition in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var deployment = transition.Deployment;
                var detail = transition.Misconfigured ? " (misconfigured)" : "";
                var text = $"OpenAI load balancer: {deployment.AccountName}/{deployment.DeploymentName} " +
                    $"in {deployment.Region}: {transition.Previous} → {transition.Current}{detail}";
                using var response = await clients.CreateClient("slack").PostAsJsonAsync(options.Value.SlackWebhookUrl,
                    new { text }, timeout.Token);
                if (!response.IsSuccessStatusCode)
                    logger.LogWarning("Slack alert delivery failed with HTTP {StatusCode}", (int)response.StatusCode);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Exceptions can contain the webhook URL. Never log exception details.
                logger.LogWarning("Slack alert delivery failed");
            }
        }
    }

    private static bool ShouldAlert(HealthTransition transition) =>
        transition.Misconfigured || transition.Current is DeploymentHealth.Open or DeploymentHealth.Degraded ||
        (transition.Previous == DeploymentHealth.Open && transition.Current != DeploymentHealth.Disabled) ||
        (transition.Previous == DeploymentHealth.Degraded && transition.Current == DeploymentHealth.Healthy) ||
        (transition.Previous == DeploymentHealth.Disabled && !transition.Misconfigured);
}
