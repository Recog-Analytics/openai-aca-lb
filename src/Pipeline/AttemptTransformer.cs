using System.Net.Http.Headers;
using System.Text.Json;
using openai_loadbalancer.Health;
using openai_loadbalancer.Routing;
using Yarp.ReverseProxy.Forwarder;

namespace openai_loadbalancer.Pipeline;

internal sealed class AttemptTransformer(Deployment deployment, RequestInput input, string token, int attemptNumber,
    HealthAttempt healthAttempt, TimeProvider clock, CancellationTokenSource ttfbTimer, CancellationTokenSource deadlineTimer,
    Func<bool> retry, Action<TimeSpan> recordTtfb, Action<TimeSpan> recordThrottle) : HttpTransformer
{
    private long sentAt;
    public bool HeadersReceived { get; private set; }
    public bool Retry { get; private set; }
    public HealthOutcome Outcome { get; private set; } = HealthOutcome.Ignored;

    public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage request,
        string destinationPrefix, CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(context, request, destinationPrefix, cancellationToken);
        request.Headers.Remove("api-key");
        request.Headers.Remove("Authorization");
        request.Headers.Remove("x-lb-data-zone");
        request.Headers.Remove("x-lb-timeout-ms");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Host = null;
        request.RequestUri = RequestUtilities.MakeDestinationAddress(destinationPrefix, input.PathFor(deployment), context.Request.QueryString);
        sentAt = clock.GetTimestamp();
    }

    public override async ValueTask<bool> TransformResponseAsync(HttpContext context, HttpResponseMessage? response,
        CancellationToken cancellationToken)
    {
        if (response == null)
            return false;
        HeadersReceived = true;
        ttfbTimer.CancelAfter(Timeout.InfiniteTimeSpan);
        // Response bodies have no deadline. A retry restores the original remaining deadline.
        deadlineTimer.CancelAfter(Timeout.InfiniteTimeSpan);
        var duration = clock.GetElapsedTime(sentAt);
        healthAttempt.RecordTtfb(duration);
        recordTtfb(duration);
        var status = (int)response.StatusCode;
        Outcome = status switch
        {
            429 => HealthOutcome.Throttled,
            500 or 502 or 503 => HealthOutcome.Failure,
            401 or 403 => HealthOutcome.Misconfigured,
            >= 200 and < 400 => HealthOutcome.Success,
            _ => HealthOutcome.Ignored
        };
        if (Outcome == HealthOutcome.Throttled)
            recordThrottle(RetryAfterParser.Parse(Header(response, "retry-after-ms"), Header(response, "Retry-After"), clock.GetUtcNow()));
        if (status == 404 && await IsDeploymentNotFoundAsync(response, cancellationToken))
            Outcome = HealthOutcome.Misconfigured;

        if (Outcome is HealthOutcome.Throttled or HealthOutcome.Misconfigured or HealthOutcome.Failure)
        {
            healthAttempt.Complete(Outcome, Header(response, "retry-after-ms"), Header(response, "Retry-After"));
            Retry = retry();
            if (Retry)
                return false;
        }

        await base.TransformResponseAsync(context, response, cancellationToken);
        context.Response.Headers["x-lb-deployment"] = deployment.DeploymentName;
        context.Response.Headers["x-lb-region"] = deployment.Region;
        context.Response.Headers["x-lb-attempts"] = attemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task<bool> IsDeploymentNotFoundAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (Header(response, "x-ms-error-code") == "DeploymentNotFound")
            return true;
        if (response.Content.Headers.ContentType?.MediaType is not "application/json")
            return false;

        // Inspect only a bounded JSON error prefix. SSE and all successful bodies remain untouched.
        var original = response.Content;
        var stream = await original.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
                break;
            length += read;
        }
        var content = new StreamContent(new PrefixStream(buffer.AsMemory(0, length), stream, original));
        foreach (var header in original.Headers)
            content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = content;
        try
        {
            using var json = JsonDocument.Parse(buffer.AsMemory(0, length));
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.String && code.GetString() == "DeploymentNotFound";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class PrefixStream(ReadOnlyMemory<byte> prefix, Stream stream, HttpContent owner) : Stream
    {
        private ReadOnlyMemory<byte> remaining = prefix;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (remaining.IsEmpty)
                return stream.ReadAsync(buffer, cancellationToken);
            var count = Math.Min(buffer.Length, remaining.Length);
            remaining[..count].CopyTo(buffer);
            remaining = remaining[count..];
            return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                owner.Dispose();
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
