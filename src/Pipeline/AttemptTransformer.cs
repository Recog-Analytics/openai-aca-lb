using System.Net.Http.Headers;
using System.Text.Json;
using openai_loadbalancer.Health;
using openai_loadbalancer.Routing;
using Yarp.ReverseProxy.Forwarder;

namespace openai_loadbalancer.Pipeline;

internal sealed class AttemptTransformer(Deployment deployment, RequestInput input, string token, int attemptNumber,
    HealthAttempt healthAttempt, TimeProvider clock, CancellationTokenSource ttfbTimer, CancellationTokenSource deadlineTimer,
    Func<bool> retry, Action<TimeSpan> recordTtfb, Action<TimeSpan> recordThrottle, Func<TimeSpan>? remaining = null) : HttpTransformer
{
    private long sentAt;
    public bool HeadersReceived { get; private set; }
    public bool Retry { get; private set; }
    public HealthOutcome Outcome { get; private set; } = HealthOutcome.Ignored;
    public HealthOutcome? CompletedOutcome { get; private set; }
    public int? Status { get; private set; }
    public TimeSpan? Ttfb { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? BackendRequestId { get; private set; }

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
        Ttfb = duration;
        // Non-streaming headers arrive after the whole completion, so only streaming TTFB feeds degradation.
        if (input.Streaming)
            healthAttempt.RecordTtfb(duration);
        recordTtfb(duration);
        var status = (int)response.StatusCode;
        Status = status;
        BackendRequestId = RequestId(Header(response, "apim-request-id")) ?? RequestId(Header(response, "x-request-id"));
        if (status is < 200 or > 299 && response.Content.Headers.ContentType?.MediaType is { } mediaType &&
            (mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.Ordinal)))
            // Only a 404 whose header does not already name the missing deployment needs its body to decide routing.
            await InspectErrorAsync(response, status == 404 && Header(response, "x-ms-error-code") != "DeploymentNotFound", cancellationToken);
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
        if (status == 404 && (Header(response, "x-ms-error-code") == "DeploymentNotFound" || ErrorCode == "DeploymentNotFound"))
            Outcome = HealthOutcome.Misconfigured;

        if (Outcome is HealthOutcome.Throttled or HealthOutcome.Misconfigured or HealthOutcome.Failure)
        {
            healthAttempt.Complete(Outcome, Header(response, "retry-after-ms"), Header(response, "Retry-After"));
            CompletedOutcome = Outcome;
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

    private static string? RequestId(string? value) =>
        value is { Length: >= 1 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')
            ? value : null;

    // Inspect only a bounded JSON error prefix and keep only code and message. SSE and all successful bodies remain untouched.
    private async Task InspectErrorAsync(HttpResponseMessage response, bool decidesRouting, CancellationToken cancellationToken)
    {
        var original = response.Content;
        var stream = await original.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[8192];
        var length = 0;
        // Timers are off once headers arrive, so a stalled error body gets one second before classification goes on without it.
        // Optional details never take the time a retry needs: at most a second, and at most half of what is left. A 404's
        // body decides whether the deployment is missing (and disabled), so it keeps the full second past the deadline.
        var budget = TimeSpan.FromSeconds(1);
        if (!decidesRouting && remaining?.Invoke() is { } left)
            budget = left <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(budget.Ticks, left.Ticks / 2));
        var expired = Task.Delay(budget, clock, cancellationToken);
        Task<int>? pending = null;
        var failed = false;
        while (length < buffer.Length)
        {
            var reading = stream.ReadAsync(buffer.AsMemory(length), cancellationToken).AsTask();
            if (await Task.WhenAny(reading, expired) != reading && !cancellationToken.IsCancellationRequested)
            {
                pending = reading;
                _ = reading.ContinueWith(task => task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                break;
            }
            int read;
            try
            {
                read = await reading;
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException ||
                exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // The details are best effort: a body that breaks off leaves the status to decide, so a 429 or 503 still retries.
                failed = true;
                break;
            }
            if (read == 0)
                break;
            length += read;
        }
        var content = new StreamContent(new PrefixStream(buffer.AsMemory(0, length), pending, buffer.AsMemory(length), stream, original));
        foreach (var header in original.Headers)
            content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = content;
        if (pending != null || failed)
            return;
        try
        {
            using var json = JsonDocument.Parse(buffer.AsMemory(0, length));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;
            var error = root.TryGetProperty("error", out var value) ? value : default;
            ErrorCode = Truncate(Text(error, "code") ?? Text(root, "code"), 64, "");
            ErrorMessage = Truncate(Text(error, "message") ?? Text(root, "message"), 300, "…");
        }
        catch (JsonException)
        {
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        } : null;

    private static string? Truncate(string? value, int maximum, string suffix)
    {
        if (value == null || value.Length <= maximum)
            return value;
        var cut = char.IsHighSurrogate(value[maximum - 1]) ? maximum - 1 : maximum;
        return value[..cut] + suffix;
    }

    // A read still pending when inspection stopped completes into pendingTarget and is served after the prefix.
    private sealed class PrefixStream(ReadOnlyMemory<byte> prefix, Task<int>? pending, ReadOnlyMemory<byte> pendingTarget,
        Stream stream, HttpContent owner) : Stream
    {
        private ReadOnlyMemory<byte> remaining = prefix;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (remaining.IsEmpty && pending != null)
            {
                var read = await pending.WaitAsync(cancellationToken);
                pending = null;
                remaining = pendingTarget[..read];
            }
            if (remaining.IsEmpty)
                return await stream.ReadAsync(buffer, cancellationToken);
            var count = Math.Min(buffer.Length, remaining.Length);
            remaining[..count].CopyTo(buffer);
            remaining = remaining[count..];
            return count;
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
