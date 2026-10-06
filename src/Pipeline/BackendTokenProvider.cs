using Azure.Core;

namespace openai_loadbalancer.Pipeline;

public interface IBackendTokenProvider
{
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken);
}

public sealed class BackendTokenProvider(TokenCredential credential, TimeProvider timeProvider) : IBackendTokenProvider, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private AccessToken token;

    public void Dispose() => gate.Dispose();

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrEmpty(token.Token) || token.ExpiresOn <= timeProvider.GetUtcNow().AddMinutes(2))
                token = await credential.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), cancellationToken);
            return token.Token;
        }
        finally
        {
            gate.Release();
        }
    }
}
