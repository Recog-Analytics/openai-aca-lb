using System.Net;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using openai_loadbalancer.Credentials;
using openai_loadbalancer.Discovery;
using openai_loadbalancer.Pipeline;

namespace openai_loadbalancer.Tests;

public class AzureCredentialTests
{
    [Fact]
    public async Task DefaultCredentialIsOneManagedIdentityInstance()
    {
        using var host = CreateHost("Production", null);
        await host.StartAsync();
        var credential = host.Services.GetRequiredService<TokenCredential>();
        Assert.IsType<ManagedIdentityCredential>(credential);
        Assert.Same(credential, host.Services.GetRequiredService<TokenCredential>());
        await host.StopAsync();
    }

    [Fact]
    public async Task FakeCredentialStartsInDevelopment()
    {
        using var host = CreateHost("Development", "Fake");
        await host.StartAsync();
        Assert.IsType<FakeTokenCredential>(host.Services.GetRequiredService<TokenCredential>());
        await host.StopAsync();
    }

    [Theory]
    [InlineData("Production", "Fake")]
    [InlineData("Staging", "Fake")]
    [InlineData("Test", "Fake")]
    [InlineData("Development", "DefaultAzureCredential")]
    [InlineData("Development", "")]
    public async Task InvalidCredentialFailsStartup(string environment, string credential)
    {
        using var host = CreateHost(environment, credential);
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public async Task DevelopmentFakeTokenIsSharedByArmAndBackend()
    {
        var builder = CreateBuilder("Development", "Fake");
        builder.Configuration["Discovery:ArmEndpoint"] = "http://localhost:5100";
        builder.Services.AddDiscovery(builder.Configuration);
        builder.Services.AddRequestPipeline(builder.Configuration);
        using var handler = new ArmHandler();
        builder.Services.AddHttpClient<IArmClient, ArmClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        using var host = builder.Build();

        var credential = host.Services.GetRequiredService<TokenCredential>();
        Assert.IsType<FakeTokenCredential>(credential);
        Assert.Same(credential, host.Services.GetRequiredService<TokenCredential>());
        var token = credential.GetToken(new TokenRequestContext(["any-scope"]), default);
        Assert.Equal(token, await credential.GetTokenAsync(new TokenRequestContext(["another-scope"]), default));
        var backendToken = await host.Services.GetRequiredService<IBackendTokenProvider>().GetTokenAsync(default);
        await host.Services.GetRequiredService<IArmClient>().GetLocationsAsync(DiscoveryRefreshTests.Subscription, default);

        Assert.Equal(token.Token, backendToken);
        Assert.Equal("Bearer " + backendToken, handler.Authorization);
    }

    [Fact]
    public async Task FakeCredentialHonorsCancellation()
    {
        var credential = new FakeTokenCredential();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new TokenRequestContext(["any-scope"]);
        Assert.Throws<OperationCanceledException>(() => credential.GetToken(context, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => credential.GetTokenAsync(context, cancellation.Token).AsTask());
    }

    private static IHost CreateHost(string environment, string? credential)
    {
        var builder = CreateBuilder(environment, credential);
        builder.Services.AddAzureCredential(builder.Configuration);
        return builder.Build();
    }

    private static HostApplicationBuilder CreateBuilder(string environment, string? credential)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        builder.Logging.ClearProviders();
        if (credential != null)
            builder.Configuration["Azure:Credential"] = credential;
        return builder;
    }

    private sealed class ArmHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"value":[]}""") });
        }
    }
}
