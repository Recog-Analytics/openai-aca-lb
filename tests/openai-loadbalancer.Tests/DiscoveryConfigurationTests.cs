using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using openai_loadbalancer.Discovery;

namespace openai_loadbalancer.Tests;

public class DiscoveryConfigurationTests
{
    [Theory]
    [InlineData("Production", "https://arm.example:8443", "00:05:00")]
    [InlineData("Development", "http://localhost:5100", "00:00:05")]
    public async Task AcceptsEndpointAndRefreshInterval(string environment, string endpoint, string interval)
    {
        using var host = CreateHost(environment, endpoint, interval);
        await host.StartAsync();
        var options = host.Services.GetRequiredService<IOptions<DiscoveryOptions>>().Value;
        Assert.Equal(endpoint, options.ArmEndpoint);
        Assert.Equal(TimeSpan.Parse(interval), options.RefreshInterval);
        await host.StopAsync();
    }

    [Fact]
    public async Task DefaultsToPublicArmAndFiveMinutes()
    {
        using var host = CreateHost("Production", null, null);
        await host.StartAsync();
        var options = host.Services.GetRequiredService<IOptions<DiscoveryOptions>>().Value;
        Assert.Equal("https://management.azure.com", options.ArmEndpoint);
        Assert.Equal(TimeSpan.FromMinutes(5), options.RefreshInterval);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("Production", "http://localhost:5100")]
    [InlineData("Staging", "http://localhost:5100")]
    [InlineData("Development", "ftp://localhost:5100")]
    [InlineData("Development", "not-a-uri")]
    [InlineData("Development", "https://user@arm.example")]
    [InlineData("Development", "https://arm.example/path")]
    [InlineData("Development", "https://arm.example?query=1")]
    [InlineData("Development", "https://arm.example#fragment")]
    public async Task InvalidEndpointFailsStartup(string environment, string endpoint)
    {
        using var host = CreateHost(environment, endpoint, null);
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("00:00:00.0000001")]
    [InlineData("50.00:00:00")]
    public async Task InvalidIntervalFailsStartup(string interval)
    {
        using var host = CreateHost("Production", null, interval);
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    private static IHost CreateHost(string environment, string? endpoint, string? interval)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        builder.Logging.ClearProviders();
        if (endpoint != null)
            builder.Configuration["Discovery:ArmEndpoint"] = endpoint;
        if (interval != null)
            builder.Configuration["Discovery:RefreshInterval"] = interval;
        builder.Services.AddDiscovery(builder.Configuration);
        // Validate startup settings without performing discovery I/O.
        builder.Services.RemoveAll<IHostedService>();
        return builder.Build();
    }
}
