using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Discovery;

public sealed class ArmClient(HttpClient httpClient, TokenCredential credential, IOptions<DiscoveryOptions> options,
    ILogger<ArmClient>? logger = null) : IArmClient
{
    private readonly ILogger<ArmClient> logger = logger ?? NullLogger<ArmClient>.Instance;
    private readonly Uri managementEndpoint = new(options.Value.ArmEndpoint, UriKind.Absolute);
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
            if (Field(item, "properties") is not { } properties || TextOrNull(properties, "provisioningState") != "Succeeded")
                continue;
            // Unsupported SKUs are skipped silently as before; a supported deployment without a usable name, model, version or
            // capacity is skipped with a warning, so one malformed entry never fails the whole refresh.
            var sku = Field(item, "sku");
            var skuName = sku is { } skuValue ? TextOrNull(skuValue, "name") : null;
            if (skuName != null && SkuMapping.FromSku(skuName) == null)
                continue;
            var name = TextOrNull(item, "name");
            var model = Field(properties, "model");
            var modelName = model is { } modelValue ? TextOrNull(modelValue, "name") : null;
            var version = model is { } versionValue ? TextOrNull(versionValue, "version") : null;
            decimal capacity = 0;
            if (skuName == null || name == null || modelName == null || version == null || Field(sku!.Value, "capacity") is not { } capacityValue ||
                capacityValue.ValueKind != JsonValueKind.Number || !capacityValue.TryGetDecimal(out capacity) || capacity < 0)
            {
                logger.LogWarning("Skipping deployment {DeploymentName} in {AccountId}: ARM returned no valid SKU, name, model, version or capacity.",
                    name ?? "(unnamed)", account.Id);
                continue;
            }
            deployments.Add(new(account.Id, account.Name, account.Endpoint, account.Region, name,
                new ModelKey(modelName, version), skuName, capacity, "Succeeded"));
        }
        return deployments;
    }

    private static JsonElement? Field(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static string? TextOrNull(JsonElement item, string name) =>
        Field(item, name) is { ValueKind: JsonValueKind.String } value && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

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
        Uri? next = new(managementEndpoint, path);
        var visited = new HashSet<Uri>();
        while (next != null)
        {
            if (next.Scheme != managementEndpoint.Scheme || next.Authority != managementEndpoint.Authority ||
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
            next = string.IsNullOrWhiteSpace(link) ? null : new Uri(managementEndpoint, link);
        }
        return items;
    }

    private static string Text(JsonElement item, string name)
    {
        var value = item.GetProperty(name).GetString();
        return !string.IsNullOrWhiteSpace(value) ? value : throw new JsonException($"ARM field '{name}' is empty.");
    }
}
