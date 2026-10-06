namespace openai_loadbalancer.Devkit;

public static class DevkitHost
{
    public static WebApplication Create(Scenario scenario, string contentRoot, string lbUrl, string key, int port = 5100)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [], ContentRootPath = contentRoot, WebRootPath = "wwwroot", EnvironmentName = "Development"
        });
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton(new ScenarioState(scenario));
        builder.Services.AddSingleton<AccountListeners>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<AccountListeners>());
        builder.Services.AddSingleton(_ => new LbClient(lbUrl, key));
        builder.Services.AddSingleton<TrafficGenerator>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<TrafficGenerator>());
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { await next(context); }
            catch (ArgumentException exception) { await Results.BadRequest(new { error = exception.Message }).ExecuteAsync(context); }
            catch (KeyNotFoundException exception) { await Results.NotFound(new { error = exception.Message }).ExecuteAsync(context); }
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        MockArm.Map(app);
        app.MapGet("/devkit/controls", (ScenarioState state) => Results.Ok(new { accounts = state.Snapshot() }));
        // Serialize mutations through listener reconciliation so concurrent controls cannot race an outage restart.
        var mutations = new SemaphoreSlim(1);
        app.Lifetime.ApplicationStopped.Register(mutations.Dispose);
        async Task<IResult> Change(Action<ScenarioState> change, ScenarioState state, AccountListeners listeners)
        {
            await mutations.WaitAsync();
            try
            {
                change(state);
                await listeners.ReconcileAsync(app.Lifetime.ApplicationStopping);
                return Results.Ok(new { accounts = state.Snapshot() });
            }
            finally { mutations.Release(); }
        }
        app.MapPut("/devkit/controls", (ControlUpdate update, ScenarioState state, AccountListeners listeners) =>
            Change(value => value.Update(update), state, listeners));
        app.MapPost("/devkit/presets/{name}", (string name, ScenarioState state, AccountListeners listeners) =>
            Change(value => value.ApplyPreset(name), state, listeners));
        app.MapPut("/devkit/deployments/{account}/{name}", (string account, string name, DeploymentDefinition deployment, ScenarioState state, AccountListeners listeners) =>
            Change(value => value.PutDeployment(account, name, deployment), state, listeners));
        app.MapDelete("/devkit/deployments/{account}/{name}", (string account, string name, ScenarioState state, AccountListeners listeners) =>
            Change(value => value.RemoveDeployment(account, name), state, listeners));
        app.MapGet("/devkit/traffic", (TrafficGenerator traffic) => Results.Ok(traffic.Snapshot()));
        app.MapPut("/devkit/traffic", (TrafficSettings settings, TrafficGenerator traffic) =>
        {
            traffic.Update(settings);
            return Results.Ok(traffic.Snapshot());
        });
        return app;
    }
}
