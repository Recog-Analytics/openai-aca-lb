using System.Net;
using System.Text;
using Azure.Core;
using openai_loadbalancer.Discovery;

namespace openai_loadbalancer.Tests;

public class ArmClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListsAccountsInSubscriptionOrResourceGroupAndFollowsPagination(bool resourceGroup)
    {
        using var handler = new FakeHandler();
        handler.Responses.Enqueue("""
            {"value":[
              {"id":"/accounts/oai","name":"oai","kind":"OpenAI","location":"westeurope","properties":{"endpoint":"https://oai.openai.azure.com/"}},
              {"kind":"SpeechServices"}
            ],"nextLink":"https://management.azure.com/next?page=2"}
            """);
        handler.Responses.Enqueue("""
            {"value":[{"id":"/accounts/foundry","name":"foundry","kind":"AIServices","location":"eastus","properties":{"endpoint":"https://foundry.cognitiveservices.azure.com/"}}]}
            """);
        using var http = new HttpClient(handler);
        var credential = new FakeCredential();
        var client = new ArmClient(http, credential);
        var scope = DiscoveryScope.Parse(resourceGroup
            ? $"/subscriptions/{DiscoveryRefreshTests.Subscription}/resourceGroups/test-rg" : DiscoveryRefreshTests.Subscription);
        var accounts = await client.GetAccountsAsync(scope, CancellationToken.None);
        Assert.Equal(new[] { "oai", "foundry" }, accounts.Select(account => account.Name));
        Assert.Equal("westeurope", accounts[0].Region);
        Assert.Equal(new Uri("https://oai.openai.azure.com/"), accounts[0].Endpoint);
        Assert.Equal($"{scope.ResourceId}/providers/Microsoft.CognitiveServices/accounts?api-version=2024-10-01", handler.Requests[0].Uri.PathAndQuery);
        Assert.Equal("/next?page=2", handler.Requests[1].Uri.PathAndQuery);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer test-token", request.Authorization));
        Assert.All(credential.Scopes, scopes => Assert.Equal(new[] { "https://management.azure.com/.default" }, scopes));
    }

    [Fact]
    public async Task ReadsModelSkuCapacityAndSkipsUnsuccessfulAndExcludedSkuDeployments()
    {
        using var handler = new FakeHandler();
        handler.Responses.Enqueue("""
            {"value":[
              {"name":"gpt4o","sku":{"name":"DataZoneStandard","capacity":150},"properties":{"provisioningState":"Succeeded","model":{"name":"gpt-4o","version":"2024-11-20"}}},
              {"name":"creating","properties":{"provisioningState":"Creating"}},
              {"name":"failed","properties":{"provisioningState":"Failed"}},
              {"name":"batch","sku":{"name":"GlobalBatch"},"properties":{"provisioningState":"Succeeded"}},
              {"name":"unknown","sku":{"name":"FutureSku"},"properties":{"provisioningState":"Succeeded"}}
            ],"nextLink":"/deployments-page-2"}
            """);
        handler.Responses.Enqueue("""
            {"value":[{"name":"global","sku":{"name":"GlobalStandard","capacity":20},"properties":{"provisioningState":"Succeeded","model":{"name":"o3","version":"2025-04-16"}}}]}
            """);
        using var http = new HttpClient(handler);
        var client = new ArmClient(http, new FakeCredential());
        var deployments = await client.GetDeploymentsAsync(DiscoveryRefreshTests.Account, CancellationToken.None);
        Assert.Equal(2, deployments.Count);
        var deployment = deployments[0];
        Assert.Equal(DiscoveryRefreshTests.Account.Id, deployment.AccountId);
        Assert.Equal(DiscoveryRefreshTests.Account.Endpoint, deployment.Endpoint);
        Assert.Equal("gpt4o", deployment.DeploymentName);
        Assert.Equal("gpt-4o", deployment.Model.Name);
        Assert.Equal("2024-11-20", deployment.Model.Version);
        Assert.Equal("DataZoneStandard", deployment.Sku);
        Assert.Equal(150, deployment.Capacity);
        Assert.Equal("Succeeded", deployment.ProvisioningState);
        Assert.Equal(DiscoveryRefreshTests.Account.Id + "/deployments?api-version=2024-10-01", handler.Requests[0].Uri.PathAndQuery);
    }

    [Fact]
    public async Task ReadsGeographyGroupsAndLeavesMissingMetadataUnknown()
    {
        using var handler = new FakeHandler();
        handler.Responses.Enqueue("""
            {"value":[
              {"name":"westeurope","metadata":{"geographyGroup":"Europe"}},
              {"name":"eastus","metadata":{"geographyGroup":"US"}},
              {"name":"unknown"},
              {"name":"null-metadata","metadata":null},
              {"name":"empty","metadata":{"geographyGroup":" "}},
              {"name":"null","metadata":{"geographyGroup":null}}
            ]}
            """);
        using var http = new HttpClient(handler);
        var client = new ArmClient(http, new FakeCredential());
        var locations = await client.GetLocationsAsync(DiscoveryRefreshTests.Subscription, CancellationToken.None);
        Assert.Equal(2, locations.Count);
        Assert.Equal("Europe", locations["WESTEUROPE"]);
        Assert.Equal("US", locations["eastus"]);
        Assert.Equal($"/subscriptions/{DiscoveryRefreshTests.Subscription}/locations?api-version=2022-12-01", handler.Requests[0].Uri.PathAndQuery);
    }

    [Theory]
    [InlineData("https://untrusted.example/page")]
    [InlineData("http://management.azure.com/page")]
    [InlineData("https://user@management.azure.com/page")]
    [InlineData("https://management.azure.com:444/page")]
    public async Task RejectsPaginationOutsideArmBeforeSendingToken(string nextLink)
    {
        using var handler = new FakeHandler();
        handler.Responses.Enqueue($$"""{"value":[],"nextLink":"{{nextLink}}"}""");
        using var http = new HttpClient(handler);
        var client = new ArmClient(http, new FakeCredential());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetLocationsAsync(DiscoveryRefreshTests.Subscription, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RejectsRepeatedPaginationUrl()
    {
        using var handler = new FakeHandler();
        handler.Responses.Enqueue($$"""{"value":[],"nextLink":"https://management.azure.com/subscriptions/{{DiscoveryRefreshTests.Subscription}}/locations?api-version=2022-12-01"}""");
        using var http = new HttpClient(handler);
        var client = new ArmClient(http, new FakeCredential());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetLocationsAsync(DiscoveryRefreshTests.Subscription, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ArmHttpErrorsPropagate(HttpStatusCode status)
    {
        using var handler = new FakeHandler { Status = status };
        handler.Responses.Enqueue("{}");
        using var http = new HttpClient(handler);
        var client = new ArmClient(http, new FakeCredential());
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLocationsAsync(DiscoveryRefreshTests.Subscription, CancellationToken.None));
        Assert.Equal(status, exception.StatusCode);
    }

    [Fact]
    public async Task MalformedArmResponseFailsRatherThanPublishingPartialData()
    {
        using var handler = new FakeHandler();
        handler.Responses.Enqueue("{}");
        using var http = new HttpClient(handler);
        var client = new ArmClient(http, new FakeCredential());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.GetLocationsAsync(DiscoveryRefreshTests.Subscription, CancellationToken.None));
    }

    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111", "/subscriptions/11111111-1111-1111-1111-111111111111")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/", "/subscriptions/11111111-1111-1111-1111-111111111111")]
    [InlineData("/SUBSCRIPTIONS/11111111-1111-1111-1111-111111111111/RESOURCEGROUPS/my-rg", "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/my-rg")]
    public void ParsesConfiguredScopeForms(string value, string resourceId)
    {
        var scope = DiscoveryScope.Parse(value);
        Assert.Equal(resourceId, scope.ResourceId);
        Assert.Equal(DiscoveryRefreshTests.Subscription, scope.SubscriptionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-id")]
    [InlineData("https://example.com/subscriptions/11111111-1111-1111-1111-111111111111")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg?query")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg%2Fother")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/..")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/providers/accounts")]
    public void RejectsInvalidOrWiderScopes(string value) => Assert.Throws<ArgumentException>(() => DiscoveryScope.Parse(value));

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Queue<string> Responses { get; } = new();
        public List<(Uri Uri, string? Authorization)> Requests { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Responses.Dequeue(), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public List<string[]> Scopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scopes.Add(requestContext.Scopes);
            return ValueTask.FromResult(new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}
