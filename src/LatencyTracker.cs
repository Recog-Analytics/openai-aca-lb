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
        var p95 = GetP95(destinationId);
        if (p95 == null)
            return false;

        return p95.Value > thresholdMs;
    }

    private void CheckStateTransition(string destinationId)
    {
        var currentlyDegraded = IsDegraded(destinationId, _thresholdMs);
        var wasDegraded = _degradedState.GetOrAdd(destinationId, false);

        if (currentlyDegraded == wasDegraded)
            return;

        _degradedState[destinationId] = currentlyDegraded;
        var p95 = GetP95(destinationId);

        if (currentlyDegraded)
        {
            _logger.LogWarning("Backend {DestinationId} DEGRADED — P95: {P95:F0}ms (threshold: {Threshold:F0}ms). Priority demoted.",
                destinationId, p95, _thresholdMs);
            SendSlackNotification(destinationId, p95, degraded: true);
        }
        else
        {
            _logger.LogInformation("Backend {DestinationId} RECOVERED — P95: {P95:F0}ms (threshold: {Threshold:F0}ms). Priority restored.",
                destinationId, p95, _thresholdMs);
            SendSlackNotification(destinationId, p95, degraded: false);
        }
    }

    private void SendSlackNotification(string destinationId, double? p95, bool degraded)
    {
        if (string.IsNullOrEmpty(_slackWebhookUrl))
            return;

        var emoji = degraded ? ":rotating_light:" : ":white_check_mark:";
        var status = degraded ? "DEGRADED" : "RECOVERED";
        var color = degraded ? "#FF0000" : "#36A64F";
        var p95Text = p95.HasValue ? $"{p95.Value / 1000:F1}s" : "N/A";

        var payload = new
        {
            attachments = new[]
            {
                new
                {
                    color,
                    blocks = new object[]
                    {
                        new { type = "section", text = new { type = "mrkdwn", text = $"{emoji} *OpenAI LB — Backend {status}*" } },
                        new { type = "section", fields = new[]
                        {
                            new { type = "mrkdwn", text = $"*Backend:*\n`{destinationId}`" },
                            new { type = "mrkdwn", text = $"*P95 Latency:*\n{p95Text}" },
                            new { type = "mrkdwn", text = $"*Threshold:*\n{_thresholdMs / 1000:F1}s" },
                            new { type = "mrkdwn", text = $"*Action:*\n{(degraded ? "Priority demoted (+100)" : "Priority restored")}" },
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
