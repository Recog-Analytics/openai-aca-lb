using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;
using openai_loadbalancer.Health;
using openai_loadbalancer.Operations;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Tests;

public class SlackAlertsTests
{
    [Fact]
    public async Task SendsFailuresAndRecoveryWithoutThrottleOrManualDrainAlerts()
    {
        using var handler = new Handler(expected: 7);
        using var client = new HttpClient(handler);
        var logger = new TestLogger<SlackAlerts>();
        using var service = Create(client, logger);
        service.Enqueue(Transition(DeploymentHealth.Healthy, DeploymentHealth.Throttled));
        service.Enqueue(Transition(DeploymentHealth.Healthy, DeploymentHealth.Disabled));
        service.Enqueue(Transition(DeploymentHealth.Healthy, DeploymentHealth.Open));
        service.Enqueue(Transition(DeploymentHealth.Open, DeploymentHealth.Degraded));
        service.Enqueue(Transition(DeploymentHealth.Degraded, DeploymentHealth.Healthy));
        service.Enqueue(Transition(DeploymentHealth.Healthy, DeploymentHealth.Disabled, misconfigured: true));
        service.Enqueue(Transition(DeploymentHealth.Disabled, DeploymentHealth.Healthy));
        service.Enqueue(Transition(DeploymentHealth.Disabled, DeploymentHealth.Throttled));
        service.Enqueue(Transition(DeploymentHealth.Disabled, DeploymentHealth.Disabled));
        await service.StartAsync(CancellationToken.None);
        await handler.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(7, handler.Bodies.Count);
        Assert.Contains(handler.Bodies, body => body.Contains("misconfigured", StringComparison.Ordinal));
        Assert.Contains(handler.Bodies, body => body.Contains("Open", StringComparison.Ordinal) && body.Contains("Degraded", StringComparison.Ordinal));
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeliveryFailuresStayIsolatedAndDoNotExposeWebhookOrExceptionDetails(bool throwException)
    {
        using var handler = new Handler(expected: 2, throwException: throwException, failFirst: true);
        using var client = new HttpClient(handler);
        var logger = new TestLogger<SlackAlerts>();
        using var service = Create(client, logger);
        service.Enqueue(Transition(DeploymentHealth.Healthy, DeploymentHealth.Open));
        service.Enqueue(Transition(DeploymentHealth.Open, DeploymentHealth.Healthy));
        await service.StartAsync(CancellationToken.None);
        await handler.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(2, handler.Bodies.Count);
        var warning = Assert.Single(logger.Entries);
        Assert.Null(warning.Exception);
        Assert.DoesNotContain("sensitive", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hooks.slack.com", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyWebhookDisablesDelivery()
    {
        using var handler = new Handler(expected: 0);
        using var client = new HttpClient(handler);
        var logger = new TestLogger<SlackAlerts>();
        using var service = new SlackAlerts(new Factory(client), Options.Create(new OperationsOptions()), logger);
        service.Enqueue(Transition(DeploymentHealth.Healthy, DeploymentHealth.Open));
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        Assert.Empty(handler.Bodies);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void FullQueueDropsAlertsWithoutBlockingTheHealthPath()
    {
        using var handler = new Handler(expected: 0);
        using var client = new HttpClient(handler);
        var logger = new TestLogger<SlackAlerts>();
        using var service = Create(client, logger);
        for (var i = 0; i < 101; i++)
            service.Enqueue(Transition(DeploymentHealth.Healthy, DeploymentHealth.Open));
        Assert.Contains("queue is full", Assert.Single(logger.Entries).Message, StringComparison.Ordinal);
    }

    private static SlackAlerts Create(HttpClient client, TestLogger<SlackAlerts> logger) =>
        new(new Factory(client), Options.Create(new OperationsOptions { SlackWebhookUrl = "https://hooks.slack.com/services/sensitive" }), logger);

    private static HealthTransition Transition(DeploymentHealth previous, DeploymentHealth current, bool misconfigured = false) =>
        new(new Deployment("account-id", "account", new Uri("https://backend.example/"), "westeurope", "deployment",
            new ModelKey("gpt-4o", "version"), "Standard", 1, "eu", 1, false), previous, current, misconfigured, TimeSpan.Zero);

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("slack", name);
            return client;
        }
    }

    private sealed class Handler(int expected, bool throwException = false, bool failFirst = false) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Bodies { get; } = new();
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (Bodies.Count == expected)
                Delivered.TrySetResult();
            if (failFirst && Bodies.Count == 1)
            {
                if (throwException)
                    throw new HttpRequestException("sensitive https://hooks.slack.com/services/sensitive");
                return new(HttpStatusCode.ServiceUnavailable);
            }
            return new(HttpStatusCode.OK);
        }
    }
}
