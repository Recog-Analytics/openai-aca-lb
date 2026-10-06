using Azure.Core;
using openai_loadbalancer.Pipeline;

namespace openai_loadbalancer.Tests;

public class BackendTokenProviderTests
{
    [Fact]
    public async Task ConcurrentRequestsShareTokenAndRefreshBeforeExpiry()
    {
        var clock = new TestClock();
        var credential = new FakeCredential(clock);
        var provider = new BackendTokenProvider(credential, clock);
        var tokens = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => provider.GetTokenAsync(default).AsTask()));
        Assert.All(tokens, token => Assert.Equal("token-1", token));
        Assert.Equal(1, credential.Calls);
        Assert.NotNull(credential.Scopes);
        Assert.Equal(["https://cognitiveservices.azure.com/.default"], credential.Scopes);
        clock.Advance(TimeSpan.FromMinutes(7));
        Assert.Equal("token-1", await provider.GetTokenAsync(default));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("token-2", await provider.GetTokenAsync(default));
        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public async Task FailedTokenRequestDoesNotPoisonCacheOrLock()
    {
        var clock = new TestClock();
        var credential = new FakeCredential(clock) { Fail = true };
        var provider = new BackendTokenProvider(credential, clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync(default).AsTask());
        credential.Fail = false;
        Assert.Equal("token-2", await provider.GetTokenAsync(default));
    }

    [Fact]
    public async Task CancelledCallerDoesNotAcquireCredential()
    {
        var credential = new FakeCredential(new TestClock());
        var provider = new BackendTokenProvider(credential, new TestClock());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetTokenAsync(cancellation.Token).AsTask());
        Assert.Equal(0, credential.Calls);
    }

    private sealed class FakeCredential(TestClock clock) : TokenCredential
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public string[]? Scopes { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            Scopes = requestContext.Scopes;
            await Task.Yield();
            if (Fail) throw new InvalidOperationException("Token unavailable");
            return new("token-" + Calls, clock.GetUtcNow().AddMinutes(10));
        }
    }
}
