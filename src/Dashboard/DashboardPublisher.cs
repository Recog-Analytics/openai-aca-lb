using System.Net.Http.Headers;
using Azure.Core;
using Microsoft.Extensions.Options;
using openai_loadbalancer.Discovery;
using openai_loadbalancer.Health;

namespace openai_loadbalancer.Dashboard;

public sealed class DashboardPublisher(DashboardEventBuffer buffer, DiscoveryState discovery, IHealthState health,
    IHttpClientFactory clients, TokenCredential credential, IOptions<DashboardPublisherOptions> options,
    TimeProvider clock, ILogger<DashboardPublisher> logger) : BackgroundService
{
    public static string Replica => Environment.GetEnvironmentVariable("CONTAINER_APP_REPLICA_NAME") ?? Environment.MachineName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        Task pending = Task.CompletedTask;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                if (pending.IsCompleted)
                    pending = PublishAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await pending; }
    }

    public async Task PublishAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var token = await credential.GetTokenAsync(new TokenRequestContext([options.Value.Audience.TrimEnd('/') + "/.default"]), cancellation.Token);
            var snapshot = discovery.Current;
            var states = health.GetSnapshot().ToDictionary(item => item.DeploymentId, StringComparer.OrdinalIgnoreCase);
            var now = clock.GetUtcNow();
            var deployments = snapshot?.Table.Deployments.Select(deployment =>
            {
                states.TryGetValue(deployment.Id, out var state);
                return new DashboardDeployment(deployment.Id, deployment.AccountName, deployment.DeploymentName,
                    deployment.Region, deployment.Model.ToString(), deployment.Tier, deployment.Zone, deployment.Weight,
                    state?.State.ToString() ?? (deployment.Disabled ? "Disabled" : "Healthy"), state?.P95?.TotalMilliseconds,
                    state?.AccountOpen ?? false, state?.RetryAfter > TimeSpan.Zero ? now + state.RetryAfter : null,
                    state?.OpenUntil, state?.HalfOpen ?? false);
            }).ToArray() ?? [];
            var events = buffer.Drain();
            var batch = new DashboardBatch(Replica, now, new(snapshot?.RefreshedAt, deployments), events.Requests, events.Counts, events.Routes);
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.IngestUrl) { Content = JsonContent.Create(batch) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var response = await clients.CreateClient("dashboard").SendAsync(request, cancellation.Token);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
                logger.LogWarning("Dashboard batch delivery failed ({FailureType}); next batch will carry fresh state.", exception.GetType().Name);
        }
    }
}
