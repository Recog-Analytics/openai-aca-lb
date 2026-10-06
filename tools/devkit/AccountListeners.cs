using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace openai_loadbalancer.Devkit;

public sealed class AccountListeners(ScenarioState state) : IHostedService, IAsyncDisposable
{
    private int disposed;
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, WebApplication> listeners = new();
    private readonly Dictionary<string, Uri> endpoints = new();

    public Uri Endpoint(string account)
    {
        lock (endpoints) return endpoints[account];
    }

    public Task StartAsync(CancellationToken cancellationToken) => ReconcileAsync(cancellationToken);

    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var account in state.Snapshot())
            {
                if (account.Outage)
                {
                    if (listeners.Remove(account.Name, out var stopped))
                    {
                        await stopped.StopAsync(cancellationToken);
                        await stopped.DisposeAsync();
                    }
                    continue;
                }
                if (listeners.ContainsKey(account.Name)) continue;
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
                builder.Logging.ClearProviders();
                builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(1));
                int port;
                lock (endpoints) port = endpoints.TryGetValue(account.Name, out var previous) ? previous.Port : account.Port;
                builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
                var app = builder.Build();
                FakeAccount.Map(app, account.Name, state);
                try { await app.StartAsync(cancellationToken); }
                catch { await app.DisposeAsync(); throw; }
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                lock (endpoints) endpoints[account.Name] = new Uri(address.Replace("127.0.0.1", "localhost") + "/");
                listeners.Add(account.Name, app);
            }
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var app in listeners.Values)
            {
                await app.StopAsync(cancellationToken);
                await app.DisposeAsync();
            }
            listeners.Clear();
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await StopAsync(CancellationToken.None);
        gate.Dispose();
    }
}
