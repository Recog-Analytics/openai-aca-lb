using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Options;
using openai_loadbalancer.Discovery;
using openai_loadbalancer.Health;
using openai_loadbalancer.Operations;
using openai_loadbalancer.Routing;
using Yarp.ReverseProxy.Forwarder;

namespace openai_loadbalancer.Pipeline;

public sealed class RequestPipeline(DiscoveryState discovery, IDeploymentSelector selector, IRetryBudget budget,
    IBackendTokenProvider tokens, IHttpForwarder forwarder, HttpMessageInvoker transport, TimeProvider clock,
    IOptions<RequestPipelineOptions> options, ILogger<RequestPipeline> logger, OperationsTelemetry telemetry, RequestHistory history)
{
    private static readonly ForwarderRequestConfig ForwarderConfig = new()
    {
        ActivityTimeout = Timeout.InfiniteTimeSpan,
        AllowResponseBuffering = false
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var observation = new RequestObservation();
        var startedAt = clock.GetUtcNow();
        var started = clock.GetTimestamp();
        try
        {
            await InvokeCoreAsync(context, observation);
        }
        catch (Exception exception)
        {
            observation.Failed = true;
            if (!context.Response.HasStarted)
                observation.Status = exception is BadHttpRequestException badRequest
                    ? badRequest.StatusCode : StatusCodes.Status500InternalServerError;
            throw;
        }
        finally
        {
            var outcome = observation.Failed ? "failure" : context.RequestAborted.IsCancellationRequested ? "client_abort" :
                context.Response.StatusCode < 400 ? "success" : "error";
            var status = observation.Status ?? context.Response.StatusCode;
            telemetry.RecordRequest(observation.Caller, observation.Model, observation.Deployment, status, outcome);
            history.Add(new RequestRecord(Guid.NewGuid().ToString("N"), startedAt, observation.Caller,
                observation.RequestedModel, observation.Model?.ToString(), observation.Zone, observation.Streaming,
                status, clock.GetElapsedTime(started).TotalMilliseconds,
                observation.Attempts.Select(attempt => new RequestAttemptRecord(attempt.Deployment.DeploymentName,
                    attempt.Deployment.AccountName, attempt.Deployment.Region, attempt.Deployment.Tier,
                    attempt.Transformer.Status, attempt.Transformer.Ttfb?.TotalMilliseconds,
                    attempt.Outcome.ToString(), attempt.RetryReason, attempt.Deployment.Id,
                    ErrorCode: attempt.Transformer.ErrorCode, ErrorMessage: attempt.Transformer.ErrorMessage,
                    BackendRequestId: attempt.Transformer.BackendRequestId)).ToArray(), outcome,
                observation.Pool?.KindName, observation.Pool?.Name,
                Operation: observation.Operation, ApiVersion: observation.ApiVersion,
                RequestBytes: observation.RequestBytes, MaxOutputTokens: observation.MaxOutputTokens));
        }
    }

    private async Task InvokeCoreAsync(HttpContext context, RequestObservation observation)
    {
        observation.Operation = RequestInput.OperationFor(context.Request.Path.Value ?? "");
        observation.ApiVersion = RequestInput.ApiVersionFor(context.Request.Query);
        var snapshot = discovery.Current;
        if (snapshot == null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        var caller = RequestInput.Authenticate(context.Request.Headers, snapshot.Callers);
        if (caller == null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        budget.RecordRequest();
        observation.Caller = caller.Name;
        var zone = RequestInput.ResolveZone(context.Request.Headers, caller);
        observation.Zone = zone;
        if (zone == null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var timeout = options.Value.OverallTimeout;
        if (context.Request.Headers.TryGetValue("x-lb-timeout-ms", out var header))
        {
            if (header.Count != 1 || !long.TryParse(header[0], NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds <= 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            timeout = TimeSpan.FromMilliseconds(Math.Min(milliseconds, timeout.TotalMilliseconds));
        }
        using var deadlineTimer = new CancellationTokenSource(timeout, clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadlineTimer.Token);
        var expiresAt = clock.GetUtcNow() + timeout;
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SelectionResult? pending = null;
        var waited = false;
        var attempts = 0;
        try
        {
            var input = await RequestInput.ReadAsync(context, deadline.Token, options.Value.MaximumBodyBytes);
            if (input == null)
                return;
            observation.RequestedModel = input.RequestedModel;
            observation.Streaming = input.Streaming;
            observation.RequestBytes = input.Body.Length;
            observation.MaxOutputTokens = input.MaxOutputTokens;
            var model = snapshot.Table.ResolveModel(input.RequestedModel);
            if (!model.Success)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = model.Error, availableVersions = model.AvailableVersions }, deadline.Token);
                return;
            }

            observation.Model = model.Key;
            observation.Pool = model.Pool;
            while (attempts < 3)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var selected = pending ?? selector.Select(snapshot.Table, model.Pool!.Value, zone, tried);
                pending = null;
                if (selected.Deployment == null || selected.Attempt == null)
                {
                    if (selected.RetryAfter is { } delay && !waited && delay < expiresAt - clock.GetUtcNow())
                    {
                        waited = true;
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(delay.TotalMilliseconds)), clock, deadline.Token);
                        continue;
                    }
                    context.Response.Clear();
                    context.Response.StatusCode = selected.RetryAfter.HasValue ? StatusCodes.Status429TooManyRequests : StatusCodes.Status503ServiceUnavailable;
                    if (selected.RetryAfter is { } retryAfter)
                        context.Response.Headers.RetryAfter = Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                    context.Response.Headers["x-lb-attempts"] = attempts.ToString(CultureInfo.InvariantCulture);
                    return;
                }

                var deployment = selected.Deployment;
                using var healthAttempt = selected.Attempt;
                string token;
                try
                {
                    token = await tokens.GetTokenAsync(deadline.Token);
                }
                catch (Exception exception) when (exception is Azure.Identity.AuthenticationFailedException or Azure.RequestFailedException)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    logger.LogError("Managed identity token acquisition failed for caller {Caller}", caller.Name);
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.Headers["x-lb-attempts"] = attempts.ToString(CultureInfo.InvariantCulture);
                    return;
                }
                tried.Add(deployment.Id);
                attempts++;
                observation.Failed = false;
                observation.Deployment = deployment;
                if (attempts > 1)
                    telemetry.RecordRetry(caller.Name, deployment);
                context.Response.Clear();
                context.Features.Set<IForwarderErrorFeature>(null);
                var body = input.BodyFor(deployment);
                using var requestBody = new MemoryStream(body, writable: false);
                context.Request.Body = requestBody;
                context.Request.ContentLength = body.Length;
                context.Request.Headers.Remove("Transfer-Encoding");
                using var ttfbTimer = new CancellationTokenSource(input.Streaming ? options.Value.StreamingTtfbTimeout
                    : options.Value.NonStreamingTtfbTimeout ?? Timeout.InfiniteTimeSpan, clock);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, ttfbTimer.Token);
                var transformer = new AttemptTransformer(deployment, input, token, attempts, healthAttempt, clock,
                    ttfbTimer, deadlineTimer, PrepareRetry, duration => telemetry.RecordTtfb(caller.Name, deployment, duration),
                    duration => telemetry.RecordThrottle(caller.Name, deployment, duration), () => expiresAt - clock.GetUtcNow());
                var attempt = new AttemptObservation(deployment, transformer);
                observation.Attempts.Add(attempt);
                var error = await forwarder.SendAsync(context, deployment.Endpoint.AbsoluteUri, transport,
                    ForwarderConfig, transformer, cancellation.Token);
                attempt.Outcome = transformer.CompletedOutcome ?? HealthOutcome.Ignored;

                // YARP also aborts the client connection after a backend body failure.
                if (context.RequestAborted.IsCancellationRequested && error != ForwarderError.ResponseBodyDestination)
                    return;
                if (transformer.Outcome == HealthOutcome.Misconfigured)
                    logger.LogError("Deployment {DeploymentId} rejected proxy authentication or does not exist; disabled until refresh", deployment.Id);
                if (transformer.Retry)
                {
                    attempt.Outcome = transformer.Outcome;
                    attempt.RetryReason = transformer.Outcome switch
                    {
                        HealthOutcome.Throttled => "throttled",
                        HealthOutcome.Misconfigured => "misconfigured",
                        _ => "backend_error"
                    };
                    continue;
                }
                if (error == ForwarderError.None)
                {
                    attempt.Outcome = transformer.Outcome;
                    healthAttempt.Complete(transformer.Outcome);
                    return;
                }

                var failure = ClassifyError(error, context.GetForwarderErrorFeature()?.Exception);
                attempt.Outcome = transformer.CompletedOutcome ?? failure;
                observation.Failed = failure != HealthOutcome.Ignored;
                healthAttempt.Complete(failure);
                if (!context.Response.HasStarted)
                {
                    context.Response.Headers["x-lb-deployment"] = deployment.DeploymentName;
                    context.Response.Headers["x-lb-region"] = deployment.Region;
                    context.Response.Headers["x-lb-attempts"] = attempts.ToString(CultureInfo.InvariantCulture);
                }
                // Headers are the retry boundary, including when the body fails before the client sees a byte.
                if (transformer.HeadersReceived || context.Response.HasStarted || failure == HealthOutcome.Ignored)
                    return;
                if (deadlineTimer.IsCancellationRequested)
                    throw new OperationCanceledException(deadline.Token);
                if (PrepareRetry())
                {
                    attempt.RetryReason = ttfbTimer.IsCancellationRequested ? "ttfb_timeout" :
                        failure == HealthOutcome.AccountFailure ? "account_failure" : "transport_error";
                    continue;
                }
                context.Response.StatusCode = ttfbTimer.IsCancellationRequested ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status502BadGateway;
                return;
            }

            bool PrepareRetry()
            {
                if (attempts >= 3 || deadline.IsCancellationRequested || clock.GetUtcNow() >= expiresAt)
                    return false;
                var next = selector.Select(snapshot.Table, model.Pool!.Value, zone, tried);
                var canWait = next.RetryAfter is { } delay && !waited && delay < expiresAt - clock.GetUtcNow();
                var remaining = expiresAt - clock.GetUtcNow();
                if ((next.Attempt == null && !canWait) || remaining <= TimeSpan.Zero || !budget.TryAcquireRetry())
                {
                    next.Attempt?.Dispose();
                    return false;
                }
                pending = next;
                deadlineTimer.CancelAfter(remaining);
                return true;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Disconnects release health probes without a failure outcome.
            observation.Failed = false;
        }
        catch (OperationCanceledException) when (deadlineTimer.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                context.Response.Headers["x-lb-attempts"] = attempts.ToString(CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            pending?.Attempt?.Dispose();
        }
    }

    private sealed class RequestObservation
    {
        public string? Caller { get; set; }
        public ModelKey? Model { get; set; }
        public RoutingPool? Pool { get; set; }
        public Deployment? Deployment { get; set; }
        public bool Failed { get; set; }
        public int? Status { get; set; }
        public string? RequestedModel { get; set; }
        public string? Zone { get; set; }
        public bool Streaming { get; set; }
        public string? Operation { get; set; }
        public string? ApiVersion { get; set; }
        public long? RequestBytes { get; set; }
        public int? MaxOutputTokens { get; set; }
        public List<AttemptObservation> Attempts { get; } = [];
    }

    private sealed class AttemptObservation(Deployment deployment, AttemptTransformer transformer)
    {
        public Deployment Deployment { get; } = deployment;
        public AttemptTransformer Transformer { get; } = transformer;
        public HealthOutcome Outcome { get; set; } = HealthOutcome.Ignored;
        public string? RetryReason { get; set; }
    }

    private static HealthOutcome ClassifyError(ForwarderError error, Exception? exception)
    {
        if (error is ForwarderError.RequestBodyClient or ForwarderError.ResponseBodyClient)
            return HealthOutcome.Ignored;
        if (error == ForwarderError.Request && (exception is HttpRequestException
            { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError } ||
            ContainsConnectionError(exception)))
            return HealthOutcome.AccountFailure;
        return HealthOutcome.Failure;
    }

    private static bool ContainsConnectionError(Exception? exception) => exception != null &&
        (exception is SocketException or AuthenticationException || ContainsConnectionError(exception.InnerException));
}
