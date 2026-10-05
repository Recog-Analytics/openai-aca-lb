using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using openai_loadbalancer.Pipeline;

namespace openai_loadbalancer.Tests;

// Request context shares the proxy harness in RequestPipelineTests.
public partial class RequestPipelineTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("/openai/deployments/gpt-4o/chat/completions", "chat.completions")]
    [InlineData("/v1/chat/completions", "chat.completions")]
    [InlineData("/openai/v1/chat/completions", "chat.completions")]
    [InlineData("/openai/deployments/gpt-4o/completions", "completions")]
    [InlineData("/openai/v1/responses", "responses")]
    [InlineData("/openai/v1/responses/resp_1/input_items", "responses")]
    [InlineData("/openai/deployments/gpt-4o/embeddings", "embeddings")]
    [InlineData("/openai/deployments/whisper/audio/transcriptions", "audio.transcriptions")]
    [InlineData("/openai/deployments/whisper/audio/translations", "audio.translations")]
    [InlineData("/openai/deployments/tts/audio/speech", "audio.speech")]
    [InlineData("/openai/deployments/dalle/images/generations", "images.generations")]
    [InlineData("/v1/images/edits", "images.edits")]
    [InlineData("/v1/images/variations", "images.variations")]
    [InlineData("/openai/v1/realtime", "realtime")]
    [InlineData("/openai/files", "files")]
    [InlineData("/openai/v1/batches/batch_1", "batches")]
    [InlineData("/v1/models", "models")]
    [InlineData("/openai/deployments/gpt-4o/extensions/chat/completions", "other")]
    [InlineData("/openai/deployments/chat", "other")]
    [InlineData("/", "other")]
    public void OperationComesFromClassicAndV1Paths(string path, string operation) =>
        Assert.Equal(operation, RequestInput.OperationFor(path));

    [Theory]
    [InlineData("?api-version=2024-10-21", "2024-10-21")]
    [InlineData("?api-version=2025-04-01-preview", "2025-04-01-preview")]
    [InlineData("?api-version=v1.preview", "v1.preview")]
    [InlineData("", null)]
    [InlineData("?api-version=", null)]
    [InlineData("?api-version=bad%20version", null)]
    [InlineData("?api-version=a/b", null)]
    [InlineData("?api-version=<script>", null)]
    [InlineData("?api-version=123456789012345678901234567890123", null)]
    [InlineData("?api-version=2024-10-21&api-version=2024-10-21", null)]
    public void ApiVersionIsRecordedOnlyWhenWellFormed(string query, string? apiVersion)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query.Length == 0 ? null : query);
        Assert.Equal(apiVersion, RequestInput.ApiVersionFor(context.Request.Query));
    }

    [Theory]
    [InlineData("{\"max_completion_tokens\":10,\"max_tokens\":20,\"max_output_tokens\":30}", 10)]
    [InlineData("{\"max_tokens\":20,\"max_output_tokens\":30}", 20)]
    [InlineData("{\"max_output_tokens\":30}", 30)]
    [InlineData("{\"max_tokens\":0}", 0)]
    [InlineData("{\"max_completion_tokens\":\"10\",\"max_tokens\":20}", 20)]
    [InlineData("{\"max_tokens\":\"20\"}", null)]
    [InlineData("{\"max_tokens\":1.5}", null)]
    [InlineData("{\"max_tokens\":1e3}", null)]
    [InlineData("{\"max_tokens\":-1}", null)]
    [InlineData("{\"max_tokens\":3000000000}", null)]
    [InlineData("{\"max_tokens\":null}", null)]
    [InlineData("{}", null)]
    [InlineData("not json", null)]
    public async Task MaxOutputTokensAcceptsOnlyIntegerLimitsInPrecedenceOrder(string body, int? expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/openai/deployments/gpt-4o/chat/completions";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var input = Assert.IsType<RequestInput>(await RequestInput.ReadAsync(context, CancellationToken.None));
        Assert.Equal(expected, input.MaxOutputTokens);
    }

    [Fact]
    public async Task SuccessBodyIsNeverInspected()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backend = await Server.StartAsync(async context =>
        {
            context.Response.ContentType = "application/json";
            context.Response.Headers["apim-request-id"] = "ok-request-1";
            await context.Response.WriteAsync("{\"error\":{\"code\":\"SECRET-CODE\",\"message\":\"SECRET-MESSAGE\"},");
            await context.Response.Body.FlushAsync();
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("\"choices\":[]}");
        });
        await using var proxy = await Proxy.StartAsync([Source(backend)]);
        try
        {
            using var request = Request(Path, "{\"max_tokens\":64}");
            // Headers reach the client while the body is still open, so the transformer did not wait for a prefix.
            using var response = await proxy.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            release.TrySetResult();
            Assert.EndsWith("\"choices\":[]}", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            release.TrySetResult();
        }
        var record = await CompletedRequestAsync(proxy);
        var attempt = Assert.Single(record.Attempts);
        Assert.Null(attempt.ErrorCode);
        Assert.Null(attempt.ErrorMessage);
        Assert.Equal("ok-request-1", attempt.BackendRequestId);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(record, WebJson));
    }

    [Fact]
    public async Task FailedAttemptRecordsOnlyErrorCodeMessageAndRequestId()
    {
        var longMessage = new string('m', 350);
        var errorBody = JsonSerializer.Serialize(new
        {
            error = new { code = "InternalServerError", message = longMessage, innererror = new { content = "SECRET-INNER" } },
            prompt = "SECRET-PROMPT",
            messages = new[] { new { role = "user", content = "SECRET-MESSAGES" } }
        });
        await using var primary = await Server.StartAsync(context =>
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers["apim-request-id"] = "apim-1234";
            context.Response.Headers["x-request-id"] = "ignored-when-apim-present";
            return context.Response.WriteAsync(errorBody);
        });
        await using var fallback = await Server.StartAsync(context =>
        {
            context.Response.Headers["x-request-id"] = "req.1:2_3";
            return context.Response.WriteAsync("SECRET-COMPLETION");
        });
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        var body = JsonSerializer.Serialize(new { messages = new[] { new { content = "SECRET-REQUEST" } }, max_completion_tokens = 50 });
        using var request = Request(Path, body);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var record = await CompletedRequestAsync(proxy);
        Assert.Equal("chat.completions", record.Operation);
        Assert.Equal("2024-10-21", record.ApiVersion);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), record.RequestBytes);
        Assert.Equal(50, record.MaxOutputTokens);
        Assert.Equal(2, record.Attempts.Count);
        Assert.Equal("InternalServerError", record.Attempts[0].ErrorCode);
        Assert.Equal(new string('m', 300) + "…", record.Attempts[0].ErrorMessage);
        Assert.Equal("apim-1234", record.Attempts[0].BackendRequestId);
        Assert.Null(record.Attempts[1].ErrorCode);
        Assert.Null(record.Attempts[1].ErrorMessage);
        Assert.Equal("req.1:2_3", record.Attempts[1].BackendRequestId);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(record, WebJson));

        using var feedRequest = Request("/admin/requests?limit=1");
        feedRequest.Method = HttpMethod.Get;
        using var feed = await proxy.Client.SendAsync(feedRequest);
        var text = await feed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET", text);
        using var json = JsonDocument.Parse(text);
        var item = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("chat.completions", item.GetProperty("operation").GetString());
        Assert.Equal("2024-10-21", item.GetProperty("apiVersion").GetString());
        Assert.Equal(Encoding.UTF8.GetByteCount(body), item.GetProperty("requestBytes").GetInt64());
        Assert.Equal(50, item.GetProperty("maxOutputTokens").GetInt32());
        var failed = item.GetProperty("attempts")[0];
        Assert.Equal("InternalServerError", failed.GetProperty("errorCode").GetString());
        Assert.Equal(301, failed.GetProperty("errorMessage").GetString()!.Length);
        Assert.Equal("apim-1234", failed.GetProperty("backendRequestId").GetString());
    }

    [Fact]
    public async Task LargeErrorBodyIsInspectedOnlyByPrefixAndReachesClientIntact()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var head = Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"BadRequest\",\"message\":\"too big\"},\"pad\":\"" + new string('a', 16 * 1024));
        var tail = Encoding.UTF8.GetBytes(new string('b', 64 * 1024) + "\"}");
        await using var backend = await Server.StartAsync(async context =>
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(head);
            await context.Response.Body.FlushAsync();
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.Body.WriteAsync(tail);
        });
        await using var proxy = await Proxy.StartAsync([Source(backend)]);
        try
        {
            using var request = Request(Path);
            using var response = await proxy.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            release.TrySetResult();
            Assert.Equal(head.Concat(tail).ToArray(), await response.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            release.TrySetResult();
        }
        var attempt = Assert.Single((await CompletedRequestAsync(proxy)).Attempts);
        // A truncated prefix is not valid JSON, so nothing is recorded from it.
        Assert.Null(attempt.ErrorCode);
        Assert.Null(attempt.ErrorMessage);
    }

    [Fact]
    public async Task StalledJsonErrorBodyStillRetriesElsewhere()
    {
        await using var primary = await Server.StartAsync(async context =>
        {
            context.Response.StatusCode = 503;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":");
            await context.Response.Body.FlushAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("fallback"));
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var request = Request(Path);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fallback", await response.Content.ReadAsStringAsync());
        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        var record = await CompletedRequestAsync(proxy);
        Assert.Equal(2, record.Attempts.Count);
        Assert.Equal(503, record.Attempts[0].Status);
        Assert.Null(record.Attempts[0].ErrorCode);
        Assert.Null(record.Attempts[0].ErrorMessage);
    }

    [Fact]
    public async Task StalledJsonErrorBodyLeavesTimeToRetryWithinAShortDeadline()
    {
        await using var primary = await Server.StartAsync(async context =>
        {
            context.Response.StatusCode = 503;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":");
            await context.Response.Body.FlushAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("fallback"));
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        using var request = Request(Path);
        // Shorter than the one-second inspection bound: inspection must leave time for the retry.
        request.Headers.Add("x-lb-timeout-ms", "800");
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fallback", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BrokenJsonErrorBodyStillRetriesElsewhere()
    {
        await using var primary = await Server.StartAsync(async context =>
        {
            context.Response.StatusCode = 429;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = 4096;
            await context.Response.WriteAsync("{\"error\":");
            await context.Response.Body.FlushAsync();
            // Break off mid-body only once the LB has the headers and is reading the body (it waits up to a second).
            await Task.Delay(300);
            context.Abort();
        });
        await using var fallback = await Server.StartAsync(context => context.Response.WriteAsync("fallback"));
        await using var proxy = await Proxy.StartAsync([
            Source(primary, "primary", sku: "ProvisionedManaged"), Source(fallback, "fallback")]);
        using var request = Request(Path);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fallback", await response.Content.ReadAsStringAsync());
        var record = await CompletedRequestAsync(proxy);
        Assert.Equal(2, record.Attempts.Count);
        Assert.Equal(429, record.Attempts[0].Status);
        Assert.Null(record.Attempts[0].ErrorCode);
    }

    [Theory]
    [InlineData("text/plain", "{\"error\":{\"code\":\"Hidden\",\"message\":\"hidden\"}}", null, null)]
    [InlineData("application/json", "not json", null, null)]
    [InlineData("application/json", "[\"array\"]", null, null)]
    [InlineData("application/problem+json", "{\"code\":400,\"message\":\"root message\"}", "400", "root message")]
    [InlineData("application/json", "{\"error\":{\"code\":{\"nested\":1},\"message\":7}}", null, "7")]
    public async Task ErrorDetailComesOnlyFromJsonErrorBodies(string contentType, string body, string? code, string? message)
    {
        await using var backend = await Server.StartAsync(context =>
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = contentType;
            context.Response.Headers["apim-request-id"] = "bad id";
            context.Response.Headers["x-request-id"] = "<script>";
            return context.Response.WriteAsync(body);
        });
        await using var proxy = await Proxy.StartAsync([Source(backend)]);
        using var request = Request(Path);
        using var response = await proxy.Client.SendAsync(request);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        var attempt = Assert.Single((await CompletedRequestAsync(proxy)).Attempts);
        Assert.Equal(code, attempt.ErrorCode);
        Assert.Equal(message, attempt.ErrorMessage);
        Assert.Null(attempt.BackendRequestId);
    }
}
