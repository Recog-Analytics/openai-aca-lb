using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace openai_loadbalancer;

public class LatencyTracker
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<double>> _latencies = new();
    private readonly ConcurrentDictionary<string, bool> _degradedState = new();
    private readonly int _windowSize;
    private readonly int _minSamples;
    private readonly double _thresholdMs;
    private readonly string? _slackWebhookUrl;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient = new();

    public LatencyTracker(LatencyConfig config, ILoggerFactory loggerFactory)
    {
        _windowSize = config.WindowSize;
        _minSamples = config.MinSamples;
        _thresholdMs = config.ThresholdMs;
        _slackWebhookUrl = config.SlackWebhookUrl;
        _logger = loggerFactory.CreateLogger<LatencyTracker>();
    }

    public void RecordLatency(string destinationId, double latencyMs)
    {
        var queue = _latencies.GetOrAdd(destinationId, _ => new ConcurrentQueue<double>());
        queue.Enqueue(latencyMs);

        while (queue.Count > _windowSize)
            queue.TryDequeue(out _);

        CheckStateTransition(destinationId);
    }

    public double? GetP95(string destinationId)
    {
        if (!_latencies.TryGetValue(destinationId, out var queue))
            return null;

        var snapshot = queue.ToArray();
        if (snapshot.Length < _minSamples)
            return null;

        Array.Sort(snapshot);
        var index = (int)(snapshot.Length * 0.95);
        if (index >= snapshot.Length)
            index = snapshot.Length - 1;

        return snapshot[index];
    }

    public bool IsDegraded(string destinationId, double thresholdMs)
    {
        if (_degradedState.TryGetValue(destinationId, out var degraded) && degraded)
            return true;

        var p95 = GetP95(destinationId);
        if (p95 == null)
            return false;

        return p95.Value > thresholdMs;
    }

    private void CheckStateTransition(string destinationId)
    {
        // Once degraded, stays degraded until container restart (manual recovery by admin)
        if (_degradedState.TryGetValue(destinationId, out var alreadyDegraded) && alreadyDegraded)
            return;

        var currentlyDegraded = IsDegraded(destinationId, _thresholdMs);
        if (!currentlyDegraded)
            return;

        if (!_degradedState.TryAdd(destinationId, true))
        {
            // Another thread already transitioned this backend
            _degradedState.TryUpdate(destinationId, true, false);
            return;
        }

        var p95 = GetP95(destinationId);
        _logger.LogWarning("Backend {DestinationId} DEGRADED — P95: {P95:F0}ms (threshold: {Threshold:F0}ms). Priority demoted. Restart required to restore.",
            destinationId, p95, _thresholdMs);
        SendSlackNotification(destinationId, p95);
    }

    private void SendSlackNotification(string destinationId, double? p95)
    {
        if (string.IsNullOrEmpty(_slackWebhookUrl))
            return;

        var p95Text = p95.HasValue ? $"{p95.Value / 1000:F1}s" : "N/A";

        var payload = new
        {
            attachments = new[]
            {
                new
                {
                    color = "#FF0000",
                    blocks = new object[]
                    {
                        new { type = "section", text = new { type = "mrkdwn", text = $":rotating_light: *OpenAI LB — Backend DEGRADED*" } },
                        new { type = "section", fields = new[]
                        {
                            new { type = "mrkdwn", text = $"*Backend:*\n`{destinationId}`" },
                            new { type = "mrkdwn", text = $"*P95 Latency:*\n{p95Text}" },
                            new { type = "mrkdwn", text = $"*Threshold:*\n{_thresholdMs / 1000:F1}s" },
                            new { type = "mrkdwn", text = $"*Action:*\nPriority demoted (+100). Restart required to restore." },
                        }},
                    }
                }
            }
        };

        _ = Task.Run(async () =>
        {
            try
            {
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _httpClient.PostAsync(_slackWebhookUrl, content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send Slack notification for {DestinationId}", destinationId);
            }
        });
    }
}
