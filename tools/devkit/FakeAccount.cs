using System.Text.Json;

namespace openai_loadbalancer.Devkit;

public static class FakeAccount
{
    public static void Map(WebApplication app, string accountName, ScenarioState state)
    {
        app.MapPost("/openai/deployments/{name}/chat/completions", (HttpContext context, string name) => Respond(context, accountName, name, false, state));
        app.MapPost("/openai/deployments/{name}/embeddings", (HttpContext context, string name) => Respond(context, accountName, name, true, state));
        app.MapPost("/openai/v1/chat/completions", (HttpContext context) => Respond(context, accountName, null, false, state));
        app.MapPost("/openai/v1/embeddings", (HttpContext context) => Respond(context, accountName, null, true, state));
    }

    private static async Task Respond(HttpContext context, string accountName, string? name, bool embeddings, ScenarioState state)
    {
        var cancel = context.RequestAborted;
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancel); }
        catch (JsonException)
        {
            await Error(context, 400, "InvalidRequest", "Expected a JSON request.");
            return;
        }
        using (document)
        {
            var body = document.RootElement;
            if (body.ValueKind != JsonValueKind.Object)
            {
                await Error(context, 400, "InvalidRequest", "Expected a JSON object.");
                return;
            }
            if (name == null && body.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                name = model.GetString();
            var account = state.Snapshot().Single(item => item.Name == accountName);
            // The LB rewrites the v1 model to the concrete deployment name.
            var deployment = account.Deployments.FirstOrDefault(item => item.Name == name);
            context.Response.Headers["x-devkit-account"] = accountName;
            context.Response.Headers["x-devkit-deployment"] = name ?? "";
            context.Response.Headers["x-ratelimit-remaining-tokens"] = "100000";
            if (deployment == null)
            {
                await Error(context, 404, "DeploymentNotFound", "Unknown deployment.");
                return;
            }
            var controls = deployment.Controls;
            await Task.Delay(controls.TtfbMs + Random.Shared.Next(controls.JitterMs + 1), cancel);
            if (controls.Missing)
            {
                await Error(context, 404, "DeploymentNotFound", "Deployment is missing.");
                return;
            }
            var outcome = Random.Shared.NextDouble();
            if (outcome < controls.ThrottleRate)
            {
                context.Response.Headers["retry-after-ms"] = controls.RetryAfterMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Error(context, 429, "RateLimitExceeded", "Simulated throttling.");
                return;
            }
            if (outcome < controls.ThrottleRate + controls.ErrorRate)
            {
                await Error(context, 500, "InternalServerError", "Simulated failure.");
                return;
            }
            if (embeddings)
            {
                var count = body.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array
                    ? input.GetArrayLength() : 1;
                await context.Response.WriteAsJsonAsync(new { @object = "list", model = deployment.Model,
                    data = Enumerable.Range(0, count).Select(index => new { @object = "embedding", index,
                        embedding = new[] { 0.125, -0.25, 0.5 } }), usage = new { prompt_tokens = count, total_tokens = count } }, cancel);
                return;
            }
            var id = "chatcmpl-" + Guid.NewGuid().ToString("N");
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var streaming = body.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True;
            if (!streaming)
            {
                await context.Response.WriteAsJsonAsync(new { id, @object = "chat.completion", created, model = deployment.Model,
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = string.Concat(Enumerable.Repeat("hello ", controls.OutputTokens)).TrimEnd() }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 1, completion_tokens = controls.OutputTokens, total_tokens = controls.OutputTokens + 1 } }, cancel);
                return;
            }
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            await Chunk(context, new { id, @object = "chat.completion.chunk", created, model = deployment.Model,
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = "" }, finish_reason = (string?)null } } });
            for (var index = 0; index < controls.OutputTokens; index++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 / controls.TokensPerSecond), cancel);
                await Chunk(context, new { id, @object = "chat.completion.chunk", created, model = deployment.Model,
                    choices = new[] { new { index = 0, delta = new { content = "hello " }, finish_reason = (string?)null } } });
            }
            await Chunk(context, new { id, @object = "chat.completion.chunk", created, model = deployment.Model,
                choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } } });
            await context.Response.WriteAsync("data: [DONE]\n\n", cancel);
            await context.Response.Body.FlushAsync(cancel);
        }
    }

    private static async Task Chunk(HttpContext context, object chunk)
    {
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static Task Error(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = new { code, message } }, context.RequestAborted);
    }
}
