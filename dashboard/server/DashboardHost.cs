using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace openai_loadbalancer.Dashboard.Server;

public static class DashboardHost
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4 * 1024 * 1024);
        builder.Services.AddOptions<DashboardServerOptions>().Bind(builder.Configuration.GetSection("Dashboard"))
            .Validate<IHostEnvironment>((options, environment) => !options.DevAuth || environment.IsDevelopment(),
                "Dashboard:DevAuth is allowed only in Development.")
            .Validate(options => options.DevAuth || Guid.TryParse(options.LbIdentityObjectId, out _),
                "Dashboard:LbIdentityObjectId must identify the LB managed identity.").ValidateOnStart();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<DashboardStore>();
        builder.Services.AddHostedService<DashboardStreamService>();
        // Compresses JSON (/api/history) and static files; text/event-stream is not in the default types, so SSE still flushes per frame.
        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Path == "/healthz")
            {
                await next(context);
                return;
            }
            var ingest = string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/ingest", StringComparison.OrdinalIgnoreCase);
            if (DashboardAuth.Allowed(context.Request, app.Services.GetRequiredService<IOptions<DashboardServerOptions>>().Value, ingest))
                await next(context);
            else if (ingest)
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
            else
                context.Response.Redirect("/.auth/login/aad?post_login_redirect_uri=%2F");
        });
        app.UseResponseCompression();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapGet("/healthz", () => Results.Ok("Healthy"));
        app.MapPost("/ingest", (DashboardBatch batch, DashboardStore store) =>
        {
            if (!Valid(batch))
                return Results.BadRequest();
            store.Ingest(batch);
            return Results.Accepted();
        });
        app.MapGet("/api/stream", StreamAsync);
        app.MapGet("/api/history", (string? from, string? to, string? resolution, DashboardStore store) =>
        {
            if (!DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start) ||
                !DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end) ||
                !int.TryParse(resolution, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
                !DashboardHistory.Resolutions.Contains(seconds) || start >= end || Math.Ceiling((end - start).TotalSeconds / seconds) > 1500)
                return Results.BadRequest();
            return Results.Ok(store.History(start, end, seconds));
        });
        return app;
    }

    /// <summary>Identifiers fit the history's text limit whole: a cut ARM id would match no deployment.</summary>
    private static bool Short(string? value) => value is null || value.Length <= DashboardHistory.MaxText;

    private static bool Valid(DashboardBatch batch) =>
        !string.IsNullOrWhiteSpace(batch.Replica) && batch.Replica.Length <= 256 && batch.State != null &&
        batch.State.Deployments is { Count: <= 10000 } && batch.Requests is { Count: <= 200 } &&
        batch.Counts is { Count: <= 30000 } &&
        batch.State.Deployments.All(item => item != null && !string.IsNullOrWhiteSpace(item.Id) && Short(item.Id) &&
            DashboardStore.Severity(item.State) >= 0 && item.Weight >= 0 &&
            (item.P95TtfbMs == null || double.IsFinite(item.P95TtfbMs.Value) && item.P95TtfbMs >= 0)) &&
        batch.State.Deployments.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == batch.State.Deployments.Count &&
        batch.Requests.All(item => item != null && !string.IsNullOrWhiteSpace(item.Id) && item.Attempts is { Count: <= 3 } &&
            item.Operation is null or { Length: <= 32 } && item.ApiVersion is null or { Length: <= 32 } &&
            item.RequestBytes is null or >= 0 && item.MaxOutputTokens is null or >= 0 &&
            item.Attempts.All(attempt => attempt != null && attempt.ErrorCode is null or { Length: <= 64 } &&
                attempt.ErrorMessage is null or { Length: <= 301 } && attempt.BackendRequestId is null or { Length: <= 128 })) &&
        batch.Counts.All(item => item != null && item.Count is >= 0 and <= 1_000_000_000 &&
            !string.IsNullOrWhiteSpace(item.DeploymentId) && !string.IsNullOrWhiteSpace(item.Outcome)) &&
        batch.Routes is null or { Count: <= 30000 } &&
        (batch.Routes ?? []).All(item => item != null && item.Count is >= 0 and <= 1_000_000_000 && item.Status is >= 0 and <= 999 &&
            Short(item.Caller) && Short(item.ModelKey) && Short(item.Zone) && Short(item.PoolKind) && Short(item.Pool) &&
            item.Hops is { Count: <= 3 } && item.Hops.All(hop => hop != null && !string.IsNullOrWhiteSpace(hop.DeploymentId) &&
                !string.IsNullOrWhiteSpace(hop.Outcome) && Short(hop.DeploymentId) && Short(hop.Outcome)));

    private static async Task StreamAsync(HttpContext context, DashboardStore store)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        var subscription = store.Subscribe();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,
            context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        try
        {
            await WriteAsync("snapshot", subscription.Snapshot);
            await foreach (var frame in subscription.Channel.Reader.ReadAllAsync(cancellation.Token))
                await WriteAsync("delta", frame);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { store.Unsubscribe(subscription.Channel); }

        async Task WriteAsync(string kind, DashboardFrame frame)
        {
            await context.Response.WriteAsync($"event: {kind}\ndata: {JsonSerializer.Serialize(frame, Json)}\n\n", cancellation.Token);
            await context.Response.Body.FlushAsync(cancellation.Token);
        }
    }
}

public sealed class DashboardStreamService(DashboardStore store, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                store.Tick();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
