using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using openai_loadbalancer.Discovery;

namespace openai_loadbalancer.Tests;

public class DiscoveryHostingTests
{
    [Fact]
    public async Task AnonymousReadinessRecoversOnFiveMinuteRefreshWhileHealthStaysHealthy()
    {
        using var fixture = new DiscoveryRefreshTests.Fixture();
        fixture.Arm.Failure = new HttpRequestException("ARM unavailable");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = fixture.DirectoryPath,
            EnvironmentName = "Test"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Discovery:Scopes:0"] = DiscoveryRefreshTests.Subscription,
            ["Discovery:OverridesFilePath"] = fixture.Options.OverridesFilePath,
            ["Discovery:CallersFilePath"] = fixture.Options.CallersFilePath
        });
        builder.Services.AddDiscovery(builder.Configuration);
        builder.Services.AddSingleton<IArmClient>(fixture.Arm);
        builder.Services.AddSingleton<TimeProvider>(fixture.Clock);
        builder.Services.AddSingleton<ILogger<DiscoveryRefreshService>>(fixture.Logger);
        builder.Services.AddHealthChecks();
        await using var app = builder.Build();
        app.MapDiscoveryReadiness();
        app.MapHealthChecks("/healthz");
        await app.StartAsync();
        await fixture.Clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var state = app.Services.GetRequiredService<DiscoveryState>();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var notReady = await http.GetAsync("/readyz");
        using var healthy = await http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);

        fixture.Arm.Failure = null;
        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(state.IsReady);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => state.IsReady);
        using var ready = await http.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        var snapshot = state.Current;
        fixture.Arm.Failure = new HttpRequestException("ARM unavailable again");
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await WaitUntilAsync(() => fixture.Logger.Entries.Count(entry => entry.Level == LogLevel.Error) == 2);
        // A failed refresh must not clear readiness or replace the snapshot.
        using var stillReady = await http.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, stillReady.StatusCode);
        Assert.Same(snapshot, state.Current);
        await app.StopAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
