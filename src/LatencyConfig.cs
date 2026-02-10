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
            config.WindowSize = Convert.ToInt32(windowSize);

        var minSamples = Environment.GetEnvironmentVariable("LATENCY_MIN_SAMPLES");
        if (minSamples != null)
            config.MinSamples = Convert.ToInt32(minSamples);

        var thresholdMs = Environment.GetEnvironmentVariable("LATENCY_THRESHOLD_MS");
        if (thresholdMs != null)
            config.ThresholdMs = Convert.ToDouble(thresholdMs);

        config.SlackWebhookUrl = Environment.GetEnvironmentVariable("SLACK_WEBHOOK_URL");

        return config;
    }
}
