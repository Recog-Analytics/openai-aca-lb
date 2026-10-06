using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using openai_loadbalancer.Configuration;
using openai_loadbalancer.Discovery;
using openai_loadbalancer.Health;
using openai_loadbalancer.Pipeline;
using openai_loadbalancer.Operations;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Tests;

public partial class RequestPipelineTests
{
    private const string Model = "gpt-4o@2024-11-20";
    private const string Key = "lbk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Path = "/openai/deployments/" + Model + "/chat/completions?api-version=2024-10-21";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptsCallerKeysAndOnlyForwardsManagedIdentity(bool bearer)
    {
        var received = new TaskCompletionSource<ReceivedRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backend = await Server.StartAsync(async context =>
        {
            received.TrySetResult(await ReceivedRequest.ReadAsync(context));
            await context.Response.WriteAsync("ok");
        });
        await using var proxy = await Proxy.StartAsync([Source(backend, "actual-deployment")]);
        using var request = Request(Path, bearer: bearer);
        request.Headers.Add("x-lb-data-zone", "eu");
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Bearer managed-identity-token", captured.Authorization);
        Assert.Null(captured.ApiKey);
        Assert.Equal("/openai/deployments/actual-deployment/chat/completions", captured.Path);
        Assert.Equal("?api-version=2024-10-21", captured.Query);
        Assert.Equal("actual-deployment", Header(response, "x-lb-deployment"));
        Assert.Equal("westeurope", Header(response, "x-lb-region"));
        Assert.Equal("1", Header(response, "x-lb-attempts"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("lbk_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    [InlineData("invalid")]
    public async Task RejectsMissingOrUnknownKeysBeforeForwarding(string? key)
    {
        await using var backend = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([Source(backend)]);
        using var request = Request(Path, key: key);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, backend.RequestCount);
        using var health = await proxy.Client.GetAsync("/healthz");
        using var ready = await proxy.Client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task MetricsDescribeRetryAndHeaderLatencyWithoutCountingTwoRequests()
    {
        var measurements = new ConcurrentQueue<(string Name, double Value, string Caller)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == OperationsTelemetry.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        void Capture(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dimensions = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
            if (dimensions.GetValueOrDefault("deployment") is string id && id.StartsWith("/accounts/metric-", StringComparison.Ordinal))
                measurements.Enqueue((instrument.Name, value, (string)dimensions["caller"]!));
        }
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Capture(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Capture(instrument, value, tags));
        listener.Start();
        await using var primary = await Server.StartAsync(context =>
        {
            context.Response.StatusCode = 429;
            context.Response.Headers.RetryAfter = "10";
            return context.Response.WriteAsync("throttled");
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("ok"));
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "metric-primary", sku: "ProvisionedManaged"), Source(fallback, "metric-fallback")]);
        using var request = Request(Path);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WaitUntilAsync(() => measurements.Any(item => item.Name == "lb.requests"));
        Assert.Single(measurements, item => item.Name == "lb.requests");
        Assert.Single(measurements, item => item.Name == "lb.retries");
        Assert.Equal(2, measurements.Count(item => item.Name == "lb.ttfb"));
        Assert.Equal(10, Assert.Single(measurements, item => item.Name == "lb.throttle_time").Value);
        Assert.All(measurements, item => Assert.Equal("integration-caller", item.Caller));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    public async Task UnauthenticatedRequestsCannotIncreaseRetryBudget(string? key)
    {
        var clock = new TestClock();
        var budget = new RetryBudget(clock);
        for (var retry = 0; retry < 100; retry++)
            Assert.True(budget.TryAcquireRetry());
        await using var backend = await Server.StartAsync(context => context.Response.WriteAsync("ok"));
        await using var proxy = await Proxy.StartAsync([Source(backend)], retryBudget: budget);
        // Crossing 500 originals would raise the 20% allowance above its exhausted floor.
        for (var count = 0; count < 510; count++)
        {
            using var request = Request(Path, key: key);
            using var response = await proxy.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.False(budget.TryAcquireRetry());
        Assert.Equal(0, backend.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminStateUsesCallerAuthenticationAndOmitsSecrets(bool bearer)
    {
        await using var backend = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([Source(backend)]);
        using var anonymous = await proxy.Client.GetAsync("/admin/state");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var request = Request("/admin/state", bearer: bearer);
        request.Method = HttpMethod.Get;
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Key, text);
        Assert.DoesNotContain("sha256:", text);
        Assert.DoesNotContain("callers", text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(Model, json.RootElement.GetProperty("deployments")[0].GetProperty("model").GetString());
        Assert.Equal("Healthy", json.RootElement.GetProperty("deployments")[0].GetProperty("health").GetProperty("state").GetString());
        Assert.Equal(0, backend.RequestCount);
    }

    [Fact]
    public async Task AdminStateReportsUnavailableBeforeDiscovery()
    {
        await using var backend = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([Source(backend)], publishSnapshot: false);
        using var request = Request("/admin/state");
        request.Method = HttpMethod.Get;
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, backend.RequestCount);
    }

    [Theory]
    [InlineData("gpt-4o", 200, "gpt-4o@2024-11-20")]
    [InlineData("unknown-model", 400, null)]
    public async Task RequestFeedShowsRequestedAndResolvedV1Models(string requestedModel, int status, string? modelKey)
    {
        await using var backend = await Server.StartAsync(context => context.Response.WriteAsync("private response"));
        await using var proxy = await Proxy.StartAsync([Source(backend, "actual-deployment")]);
        using var request = Request("/v1/chat/completions",
            JsonSerializer.Serialize(new { model = requestedModel, messages = new[] { new { content = "private prompt" } } }));
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        var record = await CompletedRequestAsync(proxy);
        Assert.Equal(requestedModel, record.RequestedModel);
        Assert.Equal(modelKey, record.ModelKey);
        Assert.Equal(status, record.Status);
        Assert.Equal(status == 200 ? 1 : 0, record.Attempts.Count);
        using var feedRequest = Request("/admin/requests?limit=1");
        feedRequest.Method = HttpMethod.Get;
        using var feed = await proxy.Client.SendAsync(feedRequest);
        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
        var text = await feed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private prompt", text);
        Assert.DoesNotContain("private response", text);
        Assert.DoesNotContain(Key, text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(record.Id, Assert.Single(json.RootElement.EnumerateArray()).GetProperty("id").GetString());
        using var stateRequest = Request("/admin/state");
        stateRequest.Method = HttpMethod.Get;
        using var state = await proxy.Client.SendAsync(stateRequest);
        using var health = await proxy.Client.GetAsync("/healthz");
        using var ready = await proxy.Client.GetAsync("/readyz");
        Assert.Single(proxy.History.GetRecent(500));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(413)]
    [InlineData(500)]
    public async Task RequestFeedReportsExceptionResponseStatus(int status)
    {
        await using var backend = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        Exception exception = status == 500 ? new IOException("body read failed") : new BadHttpRequestException("bad body", status);
        await using var proxy = await Proxy.StartAsync([Source(backend)], bodyReadException: exception);
        using var request = Request(Path);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        var record = await CompletedRequestAsync(proxy);
        Assert.Equal(status, record.Status);
        Assert.Equal("failure", record.Outcome);
        Assert.Empty(record.Attempts);
        Assert.Equal(0, backend.RequestCount);
    }

    [Theory]
    [InlineData(429, "Throttled")]
    [InlineData(403, "Misconfigured")]
    public async Task BodyFailurePreservesAlreadyRecordedHealthOutcome(int status, string healthOutcome)
    {
        var abort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backend = await Server.StartAsync(async context =>
        {
            context.Response.StatusCode = status;
            context.Response.ContentLength = 100;
            await context.Response.WriteAsync("first bytes");
            await context.Response.Body.FlushAsync();
            await abort.Task.WaitAsync(context.RequestAborted);
            context.Abort();
        });
        await using var proxy = await Proxy.StartAsync([Source(backend)], retryBudget: new NoRetries());
        try
        {
            using var request = Request(Path);
            using var response = await proxy.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            abort.TrySetResult();
            await Assert.ThrowsAsync<HttpRequestException>(() => response.Content.ReadAsByteArrayAsync());
            var record = await CompletedRequestAsync(proxy);
            var attempt = Assert.Single(record.Attempts);
            Assert.Equal(status, attempt.Status);
            Assert.Equal(healthOutcome, attempt.HealthOutcome);
            Assert.Null(attempt.RetryReason);
            Assert.Equal("failure", record.Outcome);
            Assert.Equal(healthOutcome, Assert.Single(proxy.Health.Outcomes).ToString());
        }
        finally
        {
            abort.TrySetResult();
        }
    }

    [Fact]
    public async Task DefaultZoneEnforcesResidencyAndGlobalAllowsAllZones()
    {
        await using var eu = await Server.StartAsync(context => context.Response.WriteAsync("eu"));
        await using var us = await Server.StartAsync(context => context.Response.WriteAsync("us"));
        await using var proxy = await Proxy.StartAsync([
            Source(us, "us-primary", sku: "ProvisionedManaged", region: "eastus"),
            Source(eu, "eu-secondary")]);
        using var defaultRequest = Request(Path);
        using var defaultResponse = await proxy.Client.SendAsync(defaultRequest);
        Assert.Equal("eu", await defaultResponse.Content.ReadAsStringAsync());
        using var forbiddenRequest = Request(Path);
        forbiddenRequest.Headers.Add("x-lb-data-zone", "us");
        using var forbidden = await proxy.Client.SendAsync(forbiddenRequest);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var globalRequest = Request(Path);
        globalRequest.Headers.Add("x-lb-data-zone", "global");
        using var globalResponse = await proxy.Client.SendAsync(globalRequest);
        Assert.Equal("us", await globalResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, eu.RequestCount);
        Assert.Equal(1, us.RequestCount);
    }

    [Fact]
    public async Task RewritesOnlyDeploymentPathSegmentAndV1ModelField()
    {
        var captured = new ConcurrentQueue<ReceivedRequest>();
        await using var backend = await Server.StartAsync(async context =>
        {
            captured.Enqueue(await ReceivedRequest.ReadAsync(context));
            await context.Response.WriteAsync("ok");
        });
        await using var proxy = await Proxy.StartAsync([Source(backend, "actual-deployment")]);
        using var pathRequest = Request($"/openai/deployments/{Model}/extensions/{Model}?echo={Model}");
        using var pathResponse = await proxy.Client.SendAsync(pathRequest);
        Assert.Equal(HttpStatusCode.OK, pathResponse.StatusCode);
        using var v1Request = Request("/openai/v1/chat/completions", "{\"model\":\"gpt-4o@2024-11-20\",\"messages\":[{\"content\":\"gpt-4o@2024-11-20\"}],\"stream\":false}");
        using var v1Response = await proxy.Client.SendAsync(v1Request);
        Assert.Equal(HttpStatusCode.OK, v1Response.StatusCode);
        Assert.True(captured.TryDequeue(out var path));
        Assert.Equal($"/openai/deployments/actual-deployment/extensions/{Model}", path.Path);
        Assert.Equal($"?echo={Model}", path.Query);
        Assert.True(captured.TryDequeue(out var v1));
        Assert.Equal("/openai/v1/chat/completions", v1.Path);
        using var body = JsonDocument.Parse(v1.Body);
        Assert.Equal("actual-deployment", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(Model, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task DoubleEncodedSuffixCannotEscapeSelectedDeploymentOrResidency()
    {
        const string expectedPath = "/openai/deployments/selected-deployment/%2e%2e/global-deployment/chat/completions";
        var captured = new TaskCompletionSource<ReceivedRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forbiddenCalls = 0;
        await using var backend = await Server.StartAsync(async context =>
        {
            var request = await ReceivedRequest.ReadAsync(context);
            captured.TrySetResult(request);
            if (!request.Path.StartsWith("/openai/deployments/selected-deployment/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref forbiddenCalls);
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("unintended deployment");
                return;
            }
            await context.Response.WriteAsync("selected deployment");
        });
        await using var proxy = await Proxy.StartAsync([
            Source(backend, "selected-deployment"),
            Source(backend, "global-deployment", sku: "GlobalProvisionedManaged", region: "eastus")]);
        using var request = Request($"/openai/deployments/{Model}/%252e%252e/global-deployment/chat/completions");
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("selected deployment", await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedPath, (await captured.Task.WaitAsync(TimeSpan.FromSeconds(5))).Path);
        Assert.Equal("selected-deployment", Header(response, "x-lb-deployment"));
        Assert.Equal("westeurope", Header(response, "x-lb-region"));
        Assert.Equal(0, Volatile.Read(ref forbiddenCalls));
        Assert.Equal(1, backend.RequestCount);
    }

    [Fact]
    public async Task OpenAICompatibleV1PathUsesAzurePrefixAndPreservesQueryAndBody()
    {
        var captured = new TaskCompletionSource<ReceivedRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backend = await Server.StartAsync(async context =>
        {
            var request = await ReceivedRequest.ReadAsync(context);
            captured.TrySetResult(request);
            if (request.Path != "/openai/v1/chat/completions")
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync("Azure v1 prefix required");
                return;
            }
            await context.Response.WriteAsync("ok");
        });
        await using var proxy = await Proxy.StartAsync([Source(backend, "actual-deployment")]);
        using var request = Request("/v1/chat/completions?api-version=preview",
            "{\"model\":\"gpt-4o@2024-11-20\",\"messages\":[{\"content\":\"keep this message\"}],\"stream\":false}");
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        var received = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("/openai/v1/chat/completions", received.Path);
        Assert.Equal("?api-version=preview", received.Query);
        using var body = JsonDocument.Parse(received.Body);
        Assert.Equal("actual-deployment", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("keep this message", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task StreamsEachChunkBeforeBackendCompletesAndWithoutTotalDurationTimeout()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backend = await Server.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: first\n\n");
            await context.Response.Body.FlushAsync();
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("data: second\n\n");
            await context.Response.Body.FlushAsync();
        });
        await using var proxy = await Proxy.StartAsync([Source(backend)], overallTimeout: TimeSpan.FromMilliseconds(250));
        try
        {
            using var request = Request(Path, "{\"stream\":true}");
            using var response = await proxy.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                .WaitAsync(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            Assert.Equal("data: first", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("", await reader.ReadLineAsync());
            await Task.Delay(350);
            Assert.Empty(proxy.History.GetRecent(500));
            release.TrySetResult();
            Assert.Equal("data: second", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("1", Header(response, "x-lb-attempts"));
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(1, backend.RequestCount);
        var record = await CompletedRequestAsync(proxy);
        Assert.True(record.Streaming);
        Assert.Equal("success", record.Outcome);
        Assert.True(record.DurationMs >= 350);
        Assert.True(Assert.Single(record.Attempts).TtfbMs < record.DurationMs);
    }

    [Fact]
    public async Task StreamingFailureAfterFirstChunkNeverRetriesAndRecordsFailure()
    {
        var abort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broken = await Server.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: first\n\n");
            await context.Response.Body.FlushAsync();
            await abort.Task.WaitAsync(context.RequestAborted);
            context.Abort();
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(broken, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        try
        {
            using var request = Request(Path, "{\"stream\":true}");
            using var response = await proxy.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            Assert.Equal("data: first", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("", await reader.ReadLineAsync());
            abort.TrySetResult();
            await Assert.ThrowsAnyAsync<IOException>(async () => await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await WaitUntilAsync(() => proxy.Health.Outcomes.Contains(HealthOutcome.Failure));
            Assert.Equal(0, fallback.RequestCount);
            Assert.Equal(1, broken.RequestCount);
            var record = await CompletedRequestAsync(proxy);
            Assert.Equal("failure", record.Outcome);
            Assert.Equal("Failure", Assert.Single(record.Attempts).HealthOutcome);
            Assert.Null(record.Attempts[0].RetryReason);
        }
        finally
        {
            abort.TrySetResult();
        }
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task RetriesRetriableStatusAndReplaysExactBody(int status)
    {
        var bodies = new ConcurrentQueue<string>();
        await using var primary = await Server.StartAsync(async context =>
        {
            bodies.Enqueue((await ReceivedRequest.ReadAsync(context)).Body);
            context.Response.StatusCode = status;
            context.Response.Headers.RetryAfter = "120";
            await context.Response.WriteAsync("primary error must not leak");
        });
        await using var fallback = await Server.StartAsync(async context =>
        {
            bodies.Enqueue((await ReceivedRequest.ReadAsync(context)).Body);
            await context.Response.WriteAsync("fallback success");
        });
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        const string body = "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}";
        using var request = Request(Path, body);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fallback success", await response.Content.ReadAsStringAsync());
        Assert.Equal("2", Header(response, "x-lb-attempts"));
        Assert.Equal("fallback", Header(response, "x-lb-deployment"));
        Assert.Equal(new[] { body, body }, bodies.ToArray());
        Assert.Contains(status == 429 ? HealthOutcome.Throttled : HealthOutcome.Failure, proxy.Health.Outcomes);
        Assert.Equal(1, primary.RequestCount);
        Assert.Equal(1, fallback.RequestCount);
        var record = await CompletedRequestAsync(proxy);
        Assert.Equal("integration-caller", record.Caller);
        Assert.Equal(Model, record.RequestedModel);
        Assert.Equal(Model, record.ModelKey);
        Assert.Equal("eu", record.Zone);
        Assert.Equal(200, record.Status);
        Assert.Equal("success", record.Outcome);
        Assert.False(record.Streaming);
        Assert.Collection(record.Attempts, first =>
        {
            Assert.Equal("primary", first.Deployment);
            Assert.Equal("primary", first.Account);
            Assert.Equal("westeurope", first.Region);
            Assert.Equal(0, first.Tier);
            Assert.Equal(status, first.Status);
            Assert.True(first.TtfbMs >= 0);
            Assert.Equal(status == 429 ? "Throttled" : "Failure", first.HealthOutcome);
            Assert.Equal(status == 429 ? "throttled" : "backend_error", first.RetryReason);
        }, second =>
        {
            Assert.Equal("fallback", second.Deployment);
            Assert.Equal(200, second.Status);
            Assert.Equal("Success", second.HealthOutcome);
            Assert.Null(second.RetryReason);
        });
        var text = JsonSerializer.Serialize(record);
        Assert.DoesNotContain("hello", text);
        Assert.DoesNotContain(Key, text);
        Assert.DoesNotContain("managed-identity-token", text);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(413)]
    public async Task OtherClientErrorsPassThroughWithoutRetryOrHealthFailure(int status)
    {
        await using var primary = await Server.StartAsync(async context =>
        {
            context.Response.StatusCode = status;
            await context.Response.WriteAsync("client error");
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        using var request = Request(Path);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("client error", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, fallback.RequestCount);
        Assert.DoesNotContain(HealthOutcome.Failure, proxy.Health.Outcomes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreHeaderTimeoutRetriesAndCancelsSlowBackend(bool streaming)
    {
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var slow = await Server.StartAsync(async context =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) { aborted.TrySetResult(); }
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("fallback"));
        await using var proxy = await Proxy.StartAsync([
            Source(slow, "slow", sku: "ProvisionedManaged"), Source(fallback, "fallback")],
            ttfbTimeout: TimeSpan.FromMilliseconds(150));
        using var request = Request(Path, streaming ? "{\"stream\":true}" : "{}");
        using var response = await proxy.Client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fallback", await response.Content.ReadAsStringAsync());
        Assert.Equal("2", Header(response, "x-lb-attempts"));
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(HealthOutcome.Failure, proxy.Health.Outcomes);
        var record = await CompletedRequestAsync(proxy);
        Assert.Equal(streaming, record.Streaming);
        Assert.Equal(2, record.Attempts.Count);
        Assert.Null(record.Attempts[0].Status);
        Assert.Null(record.Attempts[0].TtfbMs);
        Assert.Equal("Failure", record.Attempts[0].HealthOutcome);
        Assert.Equal("ttfb_timeout", record.Attempts[0].RetryReason);
    }

    [Fact]
    public async Task ClientAbortCancelsBackendWithoutRetryOrHealthFailure()
    {
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backend = await Server.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: first\n\n");
            await context.Response.Body.FlushAsync();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) { aborted.TrySetResult(); }
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(backend, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        using var cancellation = new CancellationTokenSource();
        using var request = Request(Path, "{\"stream\":true}");
        using var response = await proxy.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        using var stream = await response.Content.ReadAsStreamAsync();
        var bytes = new byte[100];
        Assert.True(await stream.ReadAsync(bytes, cancellation.Token) > 0);
        cancellation.Cancel();
        response.Dispose();
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !proxy.Health.Outcomes.IsEmpty);
        Assert.DoesNotContain(HealthOutcome.Failure, proxy.Health.Outcomes);
        Assert.DoesNotContain(HealthOutcome.AccountFailure, proxy.Health.Outcomes);
        Assert.Equal(0, fallback.RequestCount);
        var record = await CompletedRequestAsync(proxy);
        Assert.Equal("client_abort", record.Outcome);
        Assert.Equal("Ignored", Assert.Single(record.Attempts).HealthOutcome);
        Assert.Null(record.Attempts[0].RetryReason);
    }

    [Fact]
    public async Task ClientAbortDuringRetryTokenAcquisitionClearsPreviousFailureOutcome()
    {
        var refused = await Server.StartAsync(context => context.Response.WriteAsync("unused"));
        var primary = Source(refused, "primary", sku: "ProvisionedManaged");
        await refused.DisposeAsync();
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        var tokens = new PausingRetryTokenProvider();
        await using var proxy = await Proxy.StartAsync([primary, Source(fallback, "fallback")], tokenProvider: tokens);
        using var cancellation = new CancellationTokenSource();
        using var request = Request(Path);
        var send = proxy.Client.SendAsync(request, cancellation.Token);
        await tokens.RetryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        var record = await CompletedRequestAsync(proxy);
        Assert.Equal("client_abort", record.Outcome);
        Assert.Equal("AccountFailure", Assert.Single(record.Attempts).HealthOutcome);
        Assert.Equal(0, fallback.RequestCount);
    }

    [Fact]
    public async Task AttemptsStopAtThreeAndNeverReuseDeployment()
    {
        var servers = new List<Server>();
        try
        {
            for (var index = 0; index < 4; index++)
                servers.Add(await Server.StartAsync(context =>
                {
                    context.Response.StatusCode = 500;
                    return context.Response.WriteAsync("failure");
                }));
            await using var proxy = await Proxy.StartAsync(servers.Select((server, index) => Source(server, "deployment-" + index)).ToArray());
            using var request = Request(Path);
            using var response = await proxy.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("3", Header(response, "x-lb-attempts"));
            Assert.Equal(3, servers.Sum(server => server.RequestCount));
            Assert.All(servers, server => Assert.InRange(server.RequestCount, 0, 1));
        }
        finally
        {
            foreach (var server in servers)
                await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExhaustedRetryBudgetReturnsFirstBackendError()
    {
        await using var primary = await Server.StartAsync(context =>
        {
            context.Response.StatusCode = 500;
            return context.Response.WriteAsync("first error");
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")], retryBudget: new NoRetries());
        using var request = Request(Path);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("first error", await response.Content.ReadAsStringAsync());
        Assert.Equal("1", Header(response, "x-lb-attempts"));
        Assert.Equal(0, fallback.RequestCount);
    }

    [Fact]
    public async Task RequestBodyOverDefaultLimitReturns413WithoutForwarding()
    {
        await using var backend = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([Source(backend)]);
        using var request = Request(Path, new string('x', RequestInput.DefaultMaximumBodyBytes + 1));
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, backend.RequestCount);
    }

    [Fact]
    public async Task ClientDeadlineStopsBeforeTtfbTimeoutAndDoesNotStartFallback()
    {
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var slow = await Server.StartAsync(async context =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) { aborted.TrySetResult(); }
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(slow, "slow", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        using var request = Request(Path);
        request.Headers.Add("x-lb-timeout-ms", "150");
        using var response = await proxy.Client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("1", Header(response, "x-lb-attempts"));
        Assert.Equal(0, fallback.RequestCount);
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task MisconfiguredDeploymentIsDisabledAndRequestFallsBack(int status)
    {
        await using var primary = await Server.StartAsync(context =>
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync("{\"error\":{\"code\":\"DeploymentNotFound\",\"message\":\"deployment missing\"}}");
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("fallback"));
        var source = Source(primary, "primary", sku: "ProvisionedManaged");
        await using var proxy = await Proxy.StartAsync([source, Source(fallback, "fallback")]);
        using var request = Request(Path);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fallback", await response.Content.ReadAsStringAsync());
        Assert.Equal(DeploymentHealth.Disabled,
            proxy.Health.GetSnapshot().Single(item => item.DeploymentId.EndsWith("/primary", StringComparison.Ordinal)).State);
        Assert.Contains(HealthOutcome.Misconfigured, proxy.Health.Outcomes);
    }

    [Fact]
    public async Task WaitsForPreexistingThrottleOnceWhenDeadlineAllows()
    {
        await using var backend = await Server.StartAsync(context => context.Response.WriteAsync("recovered"));
        var source = Source(backend);
        await using var proxy = await Proxy.StartAsync([source]);
        proxy.Health.TryAcquire(source.AccountId + "/deployments/" + source.DeploymentName)!
            .Complete(HealthOutcome.Throttled, retryAfterMilliseconds: "1000");
        using var request = Request(Path);
        request.Headers.Add("x-lb-timeout-ms", "3000");
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("recovered", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, backend.RequestCount);
        Assert.Equal("1", Header(response, "x-lb-attempts"));
    }

    [Fact]
    public async Task ThrottleLongerThanDeadlineReturns429WithRetryAfter()
    {
        await using var backend = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        var source = Source(backend);
        await using var proxy = await Proxy.StartAsync([source]);
        proxy.Health.TryAcquire(source.AccountId + "/deployments/" + source.DeploymentName)!
            .Complete(HealthOutcome.Throttled, retryAfter: "120");
        using var request = Request(Path);
        request.Headers.Add("x-lb-timeout-ms", "100");
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.InRange(response.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 119, 120);
        Assert.Equal(0, backend.RequestCount);
    }

    [Fact]
    public async Task RefusedConnectionOpensWholeAccountAndRetriesDifferentAccount()
    {
        var refused = await Server.StartAsync(context => context.Response.WriteAsync("unused"));
        var primary = Source(refused, "primary", sku: "ProvisionedManaged");
        await refused.DisposeAsync();
        await using var sibling = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("fallback"));
        var siblingSource = Source(sibling, "sibling") with { AccountId = primary.AccountId, AccountName = primary.AccountName };
        await using var proxy = await Proxy.StartAsync([primary, siblingSource, Source(fallback, "fallback")]);
        using var request = Request(Path);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fallback", await response.Content.ReadAsStringAsync());
        Assert.Equal("2", Header(response, "x-lb-attempts"));
        Assert.Equal(0, sibling.RequestCount);
        Assert.Contains(HealthOutcome.AccountFailure, proxy.Health.Outcomes);
        Assert.All(proxy.Health.GetSnapshot().Where(item => item.AccountId == primary.AccountId),
            item => Assert.True(item.AccountOpen));
        var record = await CompletedRequestAsync(proxy);
        Assert.Equal(2, record.Attempts.Count);
        Assert.Null(record.Attempts[0].Status);
        Assert.Null(record.Attempts[0].TtfbMs);
        Assert.Equal("AccountFailure", record.Attempts[0].HealthOutcome);
        Assert.Equal("account_failure", record.Attempts[0].RetryReason);
    }

    [Fact]
    public async Task Ordinary404ResponsePassesThroughExactBodyWithoutRetry()
    {
        const string body = "{\"error\":{\"code\":\"NotFound\",\"message\":\"unknown operation\"}}";
        await using var primary = await Server.StartAsync(context =>
        {
            context.Response.StatusCode = 404;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(body);
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        using var request = Request(Path);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, fallback.RequestCount);
        Assert.DoesNotContain(HealthOutcome.Misconfigured, proxy.Health.Outcomes);
        Assert.DoesNotContain(HealthOutcome.Failure, proxy.Health.Outcomes);
    }

    [Theory]
    [InlineData("NotFound", false)]
    [InlineData("DeploymentNotFound", true)]
    public async Task SlowJson404BodyFinishesAfterDeadlineWithoutRetry(string errorCode, bool misconfigured)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = "{\"error\":{\"code\":\"" + errorCode + "\",\"message\":\"delayed error body\"}}";
        await using var primary = await Server.StartAsync(async context =>
        {
            context.Response.StatusCode = 404;
            context.Response.ContentType = "application/json";
            await context.Response.StartAsync();
            await context.Response.Body.FlushAsync();
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync(body);
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")],
            overallTimeout: TimeSpan.FromMilliseconds(400));
        try
        {
            using var request = Request(Path, "{\"stream\":true}");
            var send = proxy.Client.SendAsync(request);
            await WaitUntilAsync(() => proxy.Health.TtfbCount > 0);
            await Task.Delay(500);
            release.TrySetResult();
            using var response = await send.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(body, await response.Content.ReadAsStringAsync());
            Assert.Equal("1", Header(response, "x-lb-attempts"));
            Assert.Equal("primary", Header(response, "x-lb-deployment"));
            Assert.Equal(0, fallback.RequestCount);
            await WaitUntilAsync(() => !proxy.Health.Outcomes.IsEmpty);
            Assert.Contains(misconfigured ? HealthOutcome.Misconfigured : HealthOutcome.Ignored, proxy.Health.Outcomes);
            Assert.DoesNotContain(HealthOutcome.Failure, proxy.Health.Outcomes);
            Assert.Equal(misconfigured ? DeploymentHealth.Disabled : DeploymentHealth.Healthy,
                proxy.Health.GetSnapshot().Single(item => item.DeploymentId.EndsWith("/primary", StringComparison.Ordinal)).State);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task BodyFailureImmediatelyAfterResponseHeadersNeverRetries()
    {
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = await Server.StartAsync(async context =>
        {
            context.Response.ContentLength = 100;
            await context.Response.StartAsync();
            await context.Response.Body.FlushAsync();
            headersSent.TrySetResult();
            await abort.Task.WaitAsync(context.RequestAborted);
            context.Abort();
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        try
        {
            using var request = Request(Path, "{\"stream\":true}");
            var send = proxy.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            await headersSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => proxy.Health.TtfbCount > 0);
            abort.TrySetResult();
            try
            {
                using var response = await send.WaitAsync(TimeSpan.FromSeconds(5));
                await response.Content.ReadAsByteArrayAsync();
                Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
                Assert.Equal("1", Header(response, "x-lb-attempts"));
                Assert.Equal("primary", Header(response, "x-lb-deployment"));
            }
            catch (HttpRequestException)
            {
                // Either a truncated response or a generated 502 exposes the backend body failure.
            }
            await WaitUntilAsync(() => !proxy.Health.Outcomes.IsEmpty);
            Assert.Contains(HealthOutcome.Failure, proxy.Health.Outcomes);
            Assert.Equal(0, fallback.RequestCount);
        }
        finally
        {
            abort.TrySetResult();
        }
    }

    [Fact]
    public async Task LegacyDeploymentNameWorksThroughAliasOnAzurePathAndV1Body()
    {
        var captured = new ConcurrentQueue<ReceivedRequest>();
        await using var backend = await Server.StartAsync(async context =>
        {
            captured.Enqueue(await ReceivedRequest.ReadAsync(context));
            await context.Response.WriteAsync("ok");
        });
        await using var proxy = await Proxy.StartAsync([Source(backend, "actual-deployment")],
            overrides: new RoutingOverrides { Aliases = new() { ["chat"] = Model } });
        using var pathRequest = Request("/openai/deployments/chat/chat/completions?api-version=2024-10-21");
        using var pathResponse = await proxy.Client.SendAsync(pathRequest);
        Assert.Equal(HttpStatusCode.OK, pathResponse.StatusCode);
        using var v1Request = Request("/openai/v1/chat/completions", "{\"model\":\"Chat\"}");
        using var v1Response = await proxy.Client.SendAsync(v1Request);
        Assert.Equal(HttpStatusCode.OK, v1Response.StatusCode);
        Assert.True(captured.TryDequeue(out var path));
        Assert.Equal("/openai/deployments/actual-deployment/chat/completions", path.Path);
        Assert.Equal("?api-version=2024-10-21", path.Query);
        Assert.True(captured.TryDequeue(out var v1));
        using var body = JsonDocument.Parse(v1.Body);
        Assert.Equal("actual-deployment", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task SixtySecondNonStreamingCompletionIsNotCutOffOrRetried()
    {
        var clock = new TestClock();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var slow = await Server.StartAsync(async context =>
        {
            received.TrySetResult();
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("completion");
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(slow, "slow", sku: "ProvisionedManaged"), Source(fallback, "fallback")], clock: clock);
        try
        {
            using var request = Request(Path);
            var send = proxy.Client.SendAsync(request);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(60));
            release.TrySetResult();
            using var response = await send.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("completion", await response.Content.ReadAsStringAsync());
            Assert.Equal("1", Header(response, "x-lb-attempts"));
            Assert.Equal(0, fallback.RequestCount);
            await WaitUntilAsync(() => !proxy.Health.Outcomes.IsEmpty);
            Assert.Equal(HealthOutcome.Success, Assert.Single(proxy.Health.Outcomes));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task NonStreamingLatencyNeverFeedsDegradation()
    {
        await using var backend = await Server.StartAsync(context => context.Response.WriteAsync("ok"));
        await using var proxy = await Proxy.StartAsync([Source(backend)]);
        for (var i = 0; i < 25; i++)
        {
            using var request = Request(Path);
            using var response = await proxy.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        await WaitUntilAsync(() => proxy.Health.Outcomes.Count == 25);
        Assert.Equal(0, proxy.Health.TtfbCount);
        var snapshot = Assert.Single(proxy.Health.GetSnapshot());
        Assert.Null(snapshot.P95);
        Assert.Equal(DeploymentHealth.Healthy, snapshot.State);

        using var streamingRequest = Request(Path, "{\"stream\":true}");
        using var streamingResponse = await proxy.Client.SendAsync(streamingRequest);
        Assert.Equal(HttpStatusCode.OK, streamingResponse.StatusCode);
        await WaitUntilAsync(() => proxy.Health.TtfbCount == 1);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("00:10:00", true)]
    [InlineData("00:10:01", false)]
    public void OverallTimeoutDefaultsTo120SecondsAndAllowsUpTo600(string? overall, bool valid)
    {
        var settings = new Dictionary<string, string?>();
        if (overall != null)
            settings["RequestPipeline:OverallTimeout"] = overall;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        using var services = new ServiceCollection().AddRequestPipeline(configuration).BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<RequestPipelineOptions>>();
        if (!valid)
        {
            Assert.Throws<OptionsValidationException>(() => options.Value);
            return;
        }
        Assert.Equal(overall == null ? TimeSpan.FromSeconds(120) : TimeSpan.Parse(overall), options.Value.OverallTimeout);
        Assert.Null(options.Value.NonStreamingTtfbTimeout);
    }

    [Fact]
    public async Task DeploymentNamePathRoutesAcrossEveryAccountWithThatName()
    {
        await using var west = await Server.StartAsync(context => { context.Response.StatusCode = 503; return Task.CompletedTask; });
        await using var france = await Server.StartAsync(context => { context.Response.StatusCode = 503; return Task.CompletedTask; });
        await using var sweden = await Server.StartAsync(context => { context.Response.StatusCode = 503; return Task.CompletedTask; });
        await using var other = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(west, "llm-gpt-4o", account: "oai-westeurope"),
            Source(france, "llm-gpt-4o", account: "oai-francecentral"),
            Source(sweden, "LLM-GPT-4O", account: "oai-swedencentral"),
            Source(other, "other-gpt-4o", account: "oai-other")]);
        using var request = Request("/openai/deployments/llm-gpt-4o/chat/completions?api-version=2024-10-21");
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("3", Header(response, "x-lb-attempts"));
        Assert.Equal(1, west.RequestCount);
        Assert.Equal(1, france.RequestCount);
        Assert.Equal(1, sweden.RequestCount);
        Assert.Equal(0, other.RequestCount);
        var record = await CompletedRequestAsync(proxy);
        Assert.Equal("deployment", record.PoolKind);
        Assert.Equal("llm-gpt-4o", record.Pool);
        Assert.Equal(Model, record.ModelKey);
    }

    [Theory]
    [InlineData("llm-gpt-4omini-public", "llm-gpt-4omini")]
    [InlineData("llm-gpt-4omini", "llm-gpt-4omini-public")]
    public async Task SameModelDeploymentNamesStaySeparatePools(string requested, string excluded)
    {
        var mini = new ModelKey("gpt-4o-mini", "2024-07-18");
        var paths = new ConcurrentQueue<string>();
        await using var requestedWest = await Server.StartAsync(context => { context.Response.StatusCode = 503; return Task.CompletedTask; });
        await using var requestedFrance = await Server.StartAsync(context => { context.Response.StatusCode = 503; return Task.CompletedTask; });
        await using var excludedWest = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var excludedFrance = await Server.StartAsync(context => context.Response.WriteAsync("unexpected"));
        await using var proxy = await Proxy.StartAsync([
            Source(requestedWest, requested, account: "oai-westeurope", model: mini),
            Source(requestedFrance, requested, account: "oai-francecentral", model: mini),
            Source(excludedWest, excluded, account: "oai-westeurope", model: mini),
            Source(excludedFrance, excluded, account: "oai-francecentral", model: mini)]);
        for (var i = 0; i < 5; i++)
        {
            using var request = Request($"/openai/deployments/{requested}/chat/completions?api-version=2024-10-21");
            using var response = await proxy.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        Assert.Equal(0, excludedWest.RequestCount + excludedFrance.RequestCount);
        Assert.True(requestedWest.RequestCount + requestedFrance.RequestCount > 0);
    }

    [Fact]
    public async Task TwentyMegabyteMultipartUploadIsForwardedVerbatim()
    {
        var audio = RandomNumberGenerator.GetBytes(20 * 1024 * 1024);
        using var form = new MultipartFormDataContent { { new ByteArrayContent(audio), "file", "speech.wav" }, { new StringContent("json"), "response_format" } };
        var sent = await form.ReadAsByteArrayAsync();
        var contentType = form.Headers.ContentType!.ToString();
        byte[]? received = null;
        string? receivedType = null;
        string? receivedPath = null;
        await using var backend = await Server.StartAsync(async context =>
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            received = buffer.ToArray();
            receivedType = context.Request.ContentType;
            receivedPath = context.Request.Path;
            await context.Response.WriteAsync("{\"text\":\"ok\"}");
        });
        await using var proxy = await Proxy.StartAsync([Source(backend, "llm-whisper", model: new("whisper", "001"))]);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/openai/deployments/llm-whisper/audio/transcriptions?api-version=2024-06-01") { Content = new ByteArrayContent(sent) };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        request.Headers.Add("api-key", Key);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/openai/deployments/llm-whisper/audio/transcriptions", receivedPath);
        Assert.Equal(contentType, receivedType);
        Assert.Equal(SHA256.HashData(sent), SHA256.HashData(received!));
    }

    [Fact]
    public async Task ConfiguredBodyLimitAboveServerDefaultAcceptsLargerBodies()
    {
        long? length = null;
        await using var backend = await Server.StartAsync(async context =>
        {
            // The fake backend has Kestrel's default 30 MB limit; only the proxy limit is under test.
            context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = null;
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            length = buffer.Length;
        });
        await using var proxy = await Proxy.StartAsync([Source(backend)], maximumBodyBytes: 40 * 1024 * 1024);
        using var request = Request("/openai/deployments/gpt-4o/audio/transcriptions", new string('x', 32 * 1024 * 1024));
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(32 * 1024 * 1024, length);
    }

    private static HttpRequestMessage Request(string path, string body = "{}", string? key = Key, bool bearer = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (key != null)
        {
            if (bearer)
                request.Headers.Authorization = new("Bearer", key);
            else
                request.Headers.Add("api-key", key);
        }
        return request;
    }

    private static string Header(HttpResponseMessage response, string name) => Assert.Single(response.Headers.GetValues(name));

    private static DiscoveredDeployment Source(Server server, string name = "backend", string sku = "Standard", string region = "westeurope",
        string? account = null, ModelKey? model = null) =>
        new("/accounts/" + (account ?? name), account ?? name, server.Address, region, name, model ?? new("gpt-4o", "2024-11-20"), sku, 100, "Succeeded");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static async Task<RequestRecord> CompletedRequestAsync(Proxy proxy)
    {
        await WaitUntilAsync(() => proxy.History.GetRecent(1).Count > 0);
        return Assert.Single(proxy.History.GetRecent(1));
    }

    private sealed record ReceivedRequest(string Path, string Query, string? Authorization, string? ApiKey, string Body)
    {
        public static async Task<ReceivedRequest> ReadAsync(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body);
            return new(context.Request.Path, context.Request.QueryString.Value ?? "",
                context.Request.Headers.Authorization.FirstOrDefault(), context.Request.Headers["api-key"].FirstOrDefault(),
                await reader.ReadToEndAsync(context.RequestAborted));
        }
    }

    private sealed class Server(WebApplication app) : IAsyncDisposable
    {
        private int requestCount;
        public Uri Address => new(app.Urls.Single());
        public int RequestCount => Volatile.Read(ref requestCount);

        public static async Task<Server> StartAsync(RequestDelegate handler)
        {
            var builder = NewBuilder();
            var app = builder.Build();
            var server = new Server(app);
            app.Run(async context =>
            {
                Interlocked.Increment(ref server.requestCount);
                await handler(context);
            });
            await app.StartAsync();
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class Proxy(WebApplication app, RecordingHealth health) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new() { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
        public RecordingHealth Health => health;
        public RequestHistory History => app.Services.GetRequiredService<RequestHistory>();

        public static async Task<Proxy> StartAsync(DiscoveredDeployment[] deployments, TimeSpan? ttfbTimeout = null,
            TimeSpan? overallTimeout = null, IRetryBudget? retryBudget = null, bool publishSnapshot = true,
            Exception? bodyReadException = null, IBackendTokenProvider? tokenProvider = null,
            RoutingOverrides? overrides = null, TimeProvider? clock = null, int? maximumBodyBytes = null)
        {
            var builder = NewBuilder();
            var health = new RecordingHealth();
            builder.Services.AddSingleton(clock ?? TimeProvider.System);
            builder.Services.AddSingleton<IHealthState>(health);
            builder.Services.AddSingleton<IRetryBudget>(retryBudget ?? new RetryBudget(TimeProvider.System));
            builder.Services.AddSingleton<DiscoveryState>();
            builder.Services.AddRequestPipeline(builder.Configuration);
            builder.Services.AddSingleton<IBackendTokenProvider>(tokenProvider ?? new FakeTokenProvider());
            builder.Services.Configure<RequestPipelineOptions>(options =>
            {
                if (ttfbTimeout.HasValue)
                    options.NonStreamingTtfbTimeout = options.StreamingTtfbTimeout = ttfbTimeout.Value;
                if (overallTimeout.HasValue)
                    options.OverallTimeout = overallTimeout.Value;
                if (maximumBodyBytes.HasValue)
                    options.MaximumBodyBytes = maximumBodyBytes.Value;
            });
            builder.Services.AddHealthChecks();
            var app = builder.Build();
            if (bodyReadException != null)
            {
                app.UseDeveloperExceptionPage();
                app.Use(async (context, next) =>
                {
                    using var body = new FailingBodyStream(bodyReadException);
                    context.Request.Body = body;
                    await next(context);
                });
            }
            var table = new RoutingTableBuilder().Build(deployments,
                new RegionGeography(new Dictionary<string, string> { ["westeurope"] = "Europe", ["eastus"] = "United States" }), overrides ?? new());
            var callers = new CallersConfiguration
            {
                Callers = [new Caller
                {
                    Name = "integration-caller", Zones = ["eu", "global"],
                    KeyHashes = ["sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key))).ToLowerInvariant()]
                }]
            };
            var state = app.Services.GetRequiredService<DiscoveryState>();
            if (publishSnapshot)
                typeof(DiscoveryState).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(state, [new DiscoverySnapshot(table, callers, DateTimeOffset.UtcNow), overrides ?? new RoutingOverrides()]);
            app.MapHealthChecks("/healthz");
            app.MapDiscoveryReadiness();
            app.MapAdminState();
            app.MapAdminRequests();
            app.MapRequestPipeline();
            await app.StartAsync();
            return new(app, health);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static WebApplicationBuilder NewBuilder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        return builder;
    }

    private sealed class FakeTokenProvider : IBackendTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult("managed-identity-token");
    }

    private sealed class FailingBodyStream(Exception exception) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(exception);
    }

    private sealed class PausingRetryTokenProvider : IBackendTokenProvider
    {
        private int calls;
        public TaskCompletionSource RetryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) > 1)
            {
                RetryStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return "managed-identity-token";
        }
    }

    private sealed class NoRetries : IRetryBudget
    {
        public void RecordRequest() { }
        public bool TryAcquireRetry() => false;
    }

    private sealed class RecordingHealth : IHealthState
    {
        private readonly HealthState inner = new(TimeProvider.System);
        private int ttfbCount;
        public int TtfbCount => Volatile.Read(ref ttfbCount);
        public ConcurrentQueue<HealthOutcome> Outcomes { get; } = new();
        public void Reconcile(RoutingTable table, IReadOnlyDictionary<string, ModelHealthOverride> overrides) => inner.Reconcile(table, overrides);
        public IReadOnlyList<DeploymentHealthSnapshot> GetSnapshot() => inner.GetSnapshot();
        public HealthAttempt? TryAcquire(string deploymentId)
        {
            var attempt = inner.TryAcquire(deploymentId);
            if (attempt == null)
                return null;
            Action<HealthOutcome, string?, string?, TimeSpan?> complete = (outcome, milliseconds, retryAfter, ttfb) =>
            {
                Outcomes.Enqueue(outcome);
                attempt.Complete(outcome, milliseconds, retryAfter, ttfb);
            };
            Action<TimeSpan> recordTtfb = ttfb =>
            {
                attempt.RecordTtfb(ttfb);
                Interlocked.Increment(ref ttfbCount);
            };
            return (HealthAttempt)typeof(HealthAttempt).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single().Invoke([complete, recordTtfb]);
        }
    }
}
