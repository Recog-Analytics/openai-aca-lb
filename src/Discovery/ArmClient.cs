using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Discovery;

public sealed class ArmClient(HttpClient httpClient, TokenCredential credential) : IArmClient
{
    private static readonly Uri ManagementEndpoint = new("https://management.azure.com");
    private static readonly TokenRequestContext TokenContext = new(["https://management.azure.com/.default"]);
    private const string CognitiveServicesApiVersion = "2024-10-01";

    public async Task<IReadOnlyList<ArmAccount>> GetAccountsAsync(DiscoveryScope scope, CancellationToken cancellationToken)
    {
        var items = await GetListAsync($"{scope.ResourceId}/providers/Microsoft.CognitiveServices/accounts?api-version={CognitiveServicesApiVersion}", cancellationToken);
        return items.Where(item => Text(item, "kind") is "OpenAI" or "AIServices")
            .Select(item => new ArmAccount(Text(item, "id"), Text(item, "name"),
                new Uri(Text(item.GetProperty("properties"), "endpoint"), UriKind.Absolute), Text(item, "location")))
            .ToArray();
    }

    public async Task<IReadOnlyList<DiscoveredDeployment>> GetDeploymentsAsync(ArmAccount account, CancellationToken cancellationToken)
    {
        var items = await GetListAsync($"{account.Id.TrimEnd('/')}/deployments?api-version={CognitiveServicesApiVersion}", cancellationToken);
        var deployments = new List<DiscoveredDeployment>();
        foreach (var item in items)
        {
            var properties = item.GetProperty("properties");
            if (Text(properties, "provisioningState") != "Succeeded")
                continue;
            var sku = item.GetProperty("sku");
            var skuName = Text(sku, "name");
            if (SkuMapping.FromSku(skuName) == null)
                continue;
            var model = properties.GetProperty("model");
            deployments.Add(new(account.Id, account.Name, account.Endpoint, account.Region, Text(item, "name"),
                new ModelKey(Text(model, "name"), Text(model, "version")), skuName,
                sku.GetProperty("capacity").GetDecimal(), "Succeeded"));
        }
        return deployments;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetLocationsAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        var items = await GetListAsync($"/subscriptions/{subscriptionId}/locations?api-version=2022-12-01", cancellationToken);
        var locations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            // Missing metadata is an unknown geography, not a failed ARM refresh.
            if (item.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
                metadata.TryGetProperty("geographyGroup", out var geography) && geography.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(geography.GetString()))
                locations[Text(item, "name")] = geography.GetString()!;
        }
        return locations;
    }

    private async Task<List<JsonElement>> GetListAsync(string path, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        Uri? next = new(ManagementEndpoint, path);
        var visited = new HashSet<Uri>();
        while (next != null)
        {
            if (next.Scheme != Uri.UriSchemeHttps || next.Authority != ManagementEndpoint.Authority ||
                next.UserInfo.Length != 0 || !visited.Add(next))
                throw new InvalidOperationException("ARM returned an invalid or repeated pagination URL.");

            var token = await credential.GetTokenAsync(TokenContext, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            items.AddRange(document.RootElement.GetProperty("value").EnumerateArray().Select(item => item.Clone()));
            var link = document.RootElement.TryGetProperty("nextLink", out var nextLink) ? nextLink.GetString() : null;
            next = string.IsNullOrWhiteSpace(link) ? null : new Uri(ManagementEndpoint, link);
        }
        return items;
    }

    private static string Text(JsonElement item, string name)
    {
        var value = item.GetProperty(name).GetString();
        return !string.IsNullOrWhiteSpace(value) ? value : throw new JsonException($"ARM field '{name}' is empty.");
    }
}
