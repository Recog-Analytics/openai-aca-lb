using Azure.Core;

namespace openai_loadbalancer.Credentials;

public sealed class FakeTokenCredential : TokenCredential
{
    private static readonly AccessToken Token = new("devkit-token", DateTimeOffset.MaxValue);

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Token;
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}
