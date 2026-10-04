using System.Globalization;

namespace openai_loadbalancer.Health;

public static class RetryAfterParser
{
    public static TimeSpan Parse(string? retryAfterMilliseconds, string? retryAfter, DateTimeOffset now)
    {
        double seconds = 10;
        if (double.TryParse(retryAfterMilliseconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) &&
            double.IsFinite(milliseconds))
            seconds = milliseconds / 1000;
        else if (double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
            seconds = value;
        else if (DateTimeOffset.TryParseExact(retryAfter, "r", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date))
            seconds = (date - now).TotalSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 120));
    }
}
