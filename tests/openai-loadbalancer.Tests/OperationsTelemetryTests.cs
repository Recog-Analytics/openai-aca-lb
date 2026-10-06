using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using openai_loadbalancer.Health;
using openai_loadbalancer.Operations;
using openai_loadbalancer.Routing;
using OpenTelemetry.Metrics;

namespace openai_loadbalancer.Tests;

public class OperationsTelemetryTests
{
    [Fact]
    public void EmitsAllMetricsWithRoutingDimensionsAndSafeLogs()
    {
        var deployment = new Deployment("/telemetry-test", "account", new Uri("https://backend.example"), "westeurope",
            "deployment", new("gpt-4o", "2024-11-20"), "Standard", 1, "eu", 100, false);
        var measurements = new ConcurrentQueue<(string Name, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == OperationsTelemetry.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        void Capture(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dimensions = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
            if (Equals(dimensions.GetValueOrDefault("deployment"), deployment.Id))
                measurements.Enqueue((instrument.Name, value, dimensions));
        }
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Capture(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Capture(instrument, value, tags));
        listener.Start();
        var logger = new TestLogger<OperationsTelemetry>();
        var alerts = new RecordingAlerts();
        using var telemetry = new OperationsTelemetry(logger, alerts);
        telemetry.RecordRequest("operator", deployment.Model, deployment, 200, "success");
        telemetry.RecordRetry("operator", deployment);
        telemetry.RecordTtfb("operator", deployment, TimeSpan.FromMilliseconds(250));
        telemetry.RecordThrottle("operator", deployment, TimeSpan.FromSeconds(10));
        telemetry.OnTransition(new(deployment, DeploymentHealth.Healthy, DeploymentHealth.Throttled, false, TimeSpan.FromSeconds(10)));

        Assert.Equal(5, measurements.Count);
        Assert.Contains(measurements, item => item.Name == "lb.requests" && item.Value == 1 && Equals(item.Tags["outcome"], "success"));
        Assert.Contains(measurements, item => item.Name == "lb.retries" && item.Value == 1);
        Assert.Contains(measurements, item => item.Name == "lb.ttfb" && item.Value == 0.25);
        Assert.Contains(measurements, item => item.Name == "lb.state_transitions" && Equals(item.Tags["state"], "Throttled"));
        Assert.Contains(measurements, item => item.Name == "lb.throttle_time" && item.Value == 10);
        foreach (var measurement in measurements)
        {
            Assert.Equal(deployment.Model.ToString(), measurement.Tags["model"]);
            Assert.Equal(deployment.Id, measurement.Tags["deployment"]);
            Assert.Equal("westeurope", measurement.Tags["region"]);
            Assert.Equal(1, measurement.Tags["tier"]);
            Assert.Contains(measurement.Tags["caller"], new[] { "operator", "unknown" });
        }
        Assert.Equal(1, alerts.Count);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("operator") && entry.Message.Contains("success"));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("Healthy to Throttled"));
    }

    [Fact]
    public void RegistrationWorksWithoutCloudExportAndSharesHealthObserver()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        Assert.Same(provider.GetRequiredService<OperationsTelemetry>(), provider.GetRequiredService<IHealthObserver>());
        Assert.NotNull(provider.GetRequiredService<MeterProvider>());
        Assert.Null(provider.GetRequiredService<IOptions<OperationsOptions>>().Value.SlackWebhookUrl);
    }

    [Theory]
    [InlineData("http://hooks.slack.com/services/secret")]
    [InlineData("https://user:secret@hooks.slack.com/services/secret")]
    [InlineData("relative")]
    public void RejectsUnsafeWebhookConfiguration(string webhook)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Operations:SlackWebhookUrl"] = webhook
        }).Build());
        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<OperationsOptions>>().Value);
        Assert.DoesNotContain(webhook, exception.Message);
    }

    private sealed class RecordingAlerts : ISlackAlerts
    {
        public int Count { get; private set; }
        public void Enqueue(HealthTransition transition) => Count++;
    }
}
