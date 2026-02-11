using System.Globalization;

namespace openai_loadbalancer;

public class LatencyConfig
{
    public int WindowSize { get; set; } = 500;
    public int MinSamples { get; set; } = 50;
    public double ThresholdMs { get; set; } = 20000;
    public string? SlackWebhookUrl { get; set; }

    public static LatencyConfig LoadFromEnvironment()
    {
        var config = new LatencyConfig();

        var windowSize = Environment.GetEnvironmentVariable("LATENCY_WINDOW_SIZE");
        if (windowSize != null)
            config.WindowSize = int.Parse(windowSize, CultureInfo.InvariantCulture);

        var minSamples = Environment.GetEnvironmentVariable("LATENCY_MIN_SAMPLES");
        if (minSamples != null)
            config.MinSamples = int.Parse(minSamples, CultureInfo.InvariantCulture);

        var thresholdMs = Environment.GetEnvironmentVariable("LATENCY_THRESHOLD_MS");
        if (thresholdMs != null)
            config.ThresholdMs = double.Parse(thresholdMs, CultureInfo.InvariantCulture);

        config.SlackWebhookUrl = Environment.GetEnvironmentVariable("SLACK_WEBHOOK_URL");

        if (config.WindowSize < 1)
            throw new ArgumentException("LATENCY_WINDOW_SIZE must be >= 1");
        if (config.MinSamples < 1)
            throw new ArgumentException("LATENCY_MIN_SAMPLES must be >= 1");
        if (config.ThresholdMs <= 0)
            throw new ArgumentException("LATENCY_THRESHOLD_MS must be > 0");
        if (config.MinSamples > config.WindowSize)
            throw new ArgumentException($"LATENCY_MIN_SAMPLES ({config.MinSamples}) must be <= LATENCY_WINDOW_SIZE ({config.WindowSize})");

        return config;
    }
}
