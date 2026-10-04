using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using openai_loadbalancer.Discovery;
using openai_loadbalancer.Pipeline;
using openai_loadbalancer.Routing;
using Yarp.ReverseProxy.Forwarder;

namespace openai_loadbalancer.Tests;

public sealed class RequestInputTests
{
    private static readonly string Key = "lbk_" + Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray())
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static readonly DiscoveredCaller Caller = new("orchestrator", ["eu", "global"],
        ["sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key))).ToLowerInvariant()]);

    [Theory]
    [InlineData("api-key")]
    [InlineData("Authorization")]
    public void AuthenticatingValidKeyAcceptsBothSdkHeaderFormats(string header)
    {
        var headers = new HeaderDictionary { [header] = header == "api-key" ? Key : "Bearer " + Key };
        Assert.Same(Caller, RequestInput.Authenticate(headers, [Caller]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AzureKey")]
    [InlineData("lbk_not-a-valid-key")]
    [InlineData("lbk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("lbk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    public void AuthenticatingMalformedKeyRejectsItEvenWithMatchingHash(string key)
    {
        var caller = Caller with { KeyHashes = ["sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))] };
        Assert.Null(RequestInput.Authenticate(new HeaderDictionary { ["api-key"] = key }, [caller]));
    }

    [Fact]
    public void AuthenticatingMissingUnknownAndAmbiguousKeysRejectsThem()
    {
        Assert.Null(RequestInput.Authenticate(new HeaderDictionary(), [Caller]));
        Assert.Null(RequestInput.Authenticate(new HeaderDictionary { ["api-key"] = Key }, []));
        var otherKey = "lbk_" + Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Null(RequestInput.Authenticate(new HeaderDictionary { ["api-key"] = Key, ["Authorization"] = "Bearer " + otherKey }, [Caller]));
        Assert.Null(RequestInput.Authenticate(new HeaderDictionary { ["api-key"] = new StringValues([Key, Key]) }, [Caller]));
        Assert.Null(RequestInput.Authenticate(new HeaderDictionary { ["api-key"] = Key, ["Authorization"] = "Basic " + Key }, [Caller]));
    }

    [Fact]
    public void AuthenticationAcceptsRotationAndIdenticalDualHeaders()
    {
        var caller = Caller with { KeyHashes = ["invalid", "sha256:" + new string('x', 64), Caller.KeyHashes[0]] };
        Assert.Same(caller, RequestInput.Authenticate(new HeaderDictionary { ["api-key"] = Key, ["Authorization"] = "bearer " + Key }, [caller]));
    }

    [Fact]
    public void ZoneDefaultsToFirstAllowedAndRejectsUnauthorizedOrMultipleValues()
    {
        Assert.Equal("eu", RequestInput.ResolveZone(new HeaderDictionary(), Caller));
        Assert.Equal("global", RequestInput.ResolveZone(new HeaderDictionary { ["x-lb-data-zone"] = "global" }, Caller));
        Assert.Null(RequestInput.ResolveZone(new HeaderDictionary { ["x-lb-data-zone"] = "us" }, Caller));
        Assert.Null(RequestInput.ResolveZone(new HeaderDictionary { ["x-lb-data-zone"] = new StringValues(["eu", "global"]) }, Caller));
        Assert.Null(RequestInput.ResolveZone(new HeaderDictionary(), Caller with { Zones = [] }));
    }

    [Theory]
    [InlineData("/v1/chat/completions")]
    [InlineData("/openai/v1/responses")]
    public async Task V1ExtractsModelAndStreamingAndRewritesOnlyTopLevelModel(string path)
    {
        var context = Context(path, """{"model":"gpt-4o@2024-11-20","stream":true,"messages":[{"model":"retain-me","content":"hello"}],"temperature":0.25}""");
        var input = Assert.IsType<RequestInput>(await RequestInput.ReadAsync(context, CancellationToken.None));
        Assert.Equal("gpt-4o@2024-11-20", input.RequestedModel);
        Assert.True(input.Streaming);
        Assert.Equal(path.StartsWith("/v1/", StringComparison.Ordinal) ? "/openai" + path : path, input.PathFor(Backend()).Value);
        using var rewritten = JsonDocument.Parse(input.BodyFor(Backend()));
        Assert.Equal("physical-model", rewritten.RootElement.GetProperty("model").GetString());
        Assert.True(rewritten.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("retain-me", rewritten.RootElement.GetProperty("messages")[0].GetProperty("model").GetString());
        Assert.Equal(0.25, rewritten.RootElement.GetProperty("temperature").GetDouble());
        using var original = JsonDocument.Parse(input.Body);
        Assert.Equal("gpt-4o@2024-11-20", original.RootElement.GetProperty("model").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid JSON")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"model\":123}")]
    [InlineData("{\"model\":\" \"}")]
    [InlineData("{\"model\":\"gpt-4o\",\"model\":\"gpt-4o\"}")]
    public async Task V1RequiresJsonObjectWithNonemptyStringModel(string body)
    {
        var context = Context("/v1/chat/completions", body);
        Assert.Null(await RequestInput.ReadAsync(context, CancellationToken.None));
        Assert.Equal(400, context.Response.StatusCode);
    }

    [Fact]
    public async Task AzureRewritesOnlyTheDeploymentSegmentAndKeepsBodyBytes()
    {
        var context = Context("/openai/deployments/gpt-4o@2024-11-20/chat/gpt-4o@2024-11-20", "{ \"stream\":true, \"model\":\"keep\" }");
        var input = Assert.IsType<RequestInput>(await RequestInput.ReadAsync(context, CancellationToken.None));
        Assert.Equal("gpt-4o@2024-11-20", input.RequestedModel);
        Assert.True(input.Streaming);
        Assert.Equal("/openai/deployments/physical-model/chat/gpt-4o@2024-11-20", input.PathFor(Backend()).Value);
        Assert.Same(input.Body, input.BodyFor(Backend()));
    }

    [Fact]
    public async Task DeploymentNamesUseSegmentEscaping()
    {
        var context = Context("/openai/deployments/gpt-4o%402024-11-20/chat/completions", "{}");
        var input = Assert.IsType<RequestInput>(await RequestInput.ReadAsync(context, CancellationToken.None));
        Assert.Equal("gpt-4o@2024-11-20", input.RequestedModel);
        Assert.Equal("/openai/deployments/physical%20model%2Fpart/chat/completions",
            input.PathFor(Backend() with { DeploymentName = "physical model/part" }).ToUriComponent());
    }

    [Fact]
    public async Task AzureSuffixIsNotDecodedAgainWhenBuildingYarpDestination()
    {
        // ASP.NET has decoded the original %252E, %252F and %255C URI components once.
        var context = Context("/openai/deployments/gpt-4o/chat/%2E%2E/%2F/%5C", "{}");
        var input = Assert.IsType<RequestInput>(await RequestInput.ReadAsync(context, CancellationToken.None));
        var destination = RequestUtilities.MakeDestinationAddress("https://backend.example", input.PathFor(Backend()), QueryString.Empty);
        Assert.Equal("/openai/deployments/physical-model/chat/%252E%252E/%252F/%255C", destination.AbsolutePath);
    }

    [Theory]
    [InlineData("/v1/chat/completions")]
    [InlineData("/openai/v1/chat/completions")]
    public async Task V1UsesAzureBackendPrefixAndPreservesQuery(string path)
    {
        var context = Context(path, "{\"model\":\"gpt-4o\"}");
        var input = Assert.IsType<RequestInput>(await RequestInput.ReadAsync(context, CancellationToken.None));
        var destination = RequestUtilities.MakeDestinationAddress("https://backend.example", input.PathFor(Backend()),
            new QueryString("?api-version=2024-06-01&tag=a%2Fb"));
        Assert.Equal("https://backend.example/openai/v1/chat/completions?api-version=2024-06-01&tag=a%2Fb", destination.AbsoluteUri);
    }

    [Theory]
    [InlineData("/openai/deployments/./chat/completions")]
    [InlineData("/openai/deployments/../chat/completions")]
    [InlineData("/openai/deployments/gpt-4o/chat/../completions")]
    [InlineData("/openai/deployments/gpt-4o/chat/./completions")]
    [InlineData("/openai/deployments/gpt-4o/chat\\completions")]
    [InlineData("/v1/../chat/completions")]
    [InlineData("/v1/./chat/completions")]
    [InlineData("/v1/chat\\completions")]
    [InlineData("/openai/v1/../chat/completions")]
    [InlineData("/openai/v1/./chat/completions")]
    [InlineData("/openai/v1/chat\\completions")]
    public async Task PathsWithDecodedDotSegmentsOrBackslashesAreRejected(string path)
    {
        var context = Context(path, "{\"model\":\"gpt-4o\"}");
        Assert.Null(await RequestInput.ReadAsync(context, CancellationToken.None));
        Assert.Equal(400, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/unknown")]
    [InlineData("/openai/deployments//chat/completions")]
    [InlineData("/v1")]
    public async Task InvalidRoutingPathReturnsBadRequest(string path)
    {
        var context = Context(path, "{}");
        Assert.Null(await RequestInput.ReadAsync(context, CancellationToken.None));
        Assert.Equal(400, context.Response.StatusCode);
    }

    [Fact]
    public async Task DeclaredOversizedBodyIsRejectedBeforeReading()
    {
        var context = Context("/openai/deployments/gpt-4o/chat/completions", "{}");
        context.Request.ContentLength = RequestInput.MaximumBodyBytes + 1;
        Assert.Null(await RequestInput.ReadAsync(context, CancellationToken.None));
        Assert.Equal(413, context.Response.StatusCode);
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public async Task ChunkedBodyLimitRejectsExtraByteWithoutTrustingContentLength()
    {
        var context = Context("/openai/deployments/gpt-4o/audio/transcriptions", "");
        context.Request.Body = new MemoryStream(new byte[RequestInput.MaximumBodyBytes + 100]);
        Assert.Null(await RequestInput.ReadAsync(context, CancellationToken.None));
        Assert.Equal(413, context.Response.StatusCode);
        Assert.Equal(RequestInput.MaximumBodyBytes + 1, context.Request.Body.Position);
    }

    [Fact]
    public async Task MaximumSizeAzureBodyIsAllowedWithoutJsonParsingRequirement()
    {
        var context = Context("/openai/deployments/gpt-4o/audio/transcriptions", "");
        context.Request.Body = new MemoryStream(new byte[RequestInput.MaximumBodyBytes]);
        var input = Assert.IsType<RequestInput>(await RequestInput.ReadAsync(context, CancellationToken.None));
        Assert.Equal(RequestInput.MaximumBodyBytes, input.Body.Length);
        Assert.False(input.Streaming);
    }

    [Fact]
    public async Task ClientCancellationIsNotConvertedToValidationError()
    {
        var context = Context("/v1/chat/completions", "{\"model\":\"gpt-4o\"}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RequestInput.ReadAsync(context, new CancellationToken(true)));
    }

    private static DefaultHttpContext Context(string path, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = new PathString(path);
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context;
    }

    private static Deployment Backend() => new("account", "account", new Uri("https://backend.example"), "westeurope",
        "physical-model", new ModelKey("gpt-4o", "2024-11-20"), "Standard", 1, "eu", 1, false);
}
