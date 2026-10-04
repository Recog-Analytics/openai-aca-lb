using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using openai_loadbalancer.Discovery;
using openai_loadbalancer.Routing;

namespace openai_loadbalancer.Pipeline;

public sealed class RequestInput
{
    public const int MaximumBodyBytes = 16 * 1024 * 1024;
    private const string DeploymentPrefix = "/openai/deployments/";
    private readonly string originalPath;
    private readonly bool v1;

    private RequestInput(string requestedModel, bool streaming, byte[] body, string path, bool v1)
    {
        RequestedModel = requestedModel;
        Streaming = streaming;
        Body = body;
        originalPath = path;
        this.v1 = v1;
    }

    public string RequestedModel { get; }
    public bool Streaming { get; }
    public byte[] Body { get; }

    public static DiscoveredCaller? Authenticate(IHeaderDictionary headers, IReadOnlyList<DiscoveredCaller> callers)
    {
        string? apiKey = null;
        string? bearerKey = null;
        if (headers.TryGetValue("api-key", out var apiKeys))
        {
            if (apiKeys.Count != 1) return null;
            apiKey = apiKeys[0];
            if (!ValidKey(apiKey)) return null;
        }
        if (headers.TryGetValue("Authorization", out var authorizations))
        {
            if (authorizations.Count != 1) return null;
            var authorization = authorizations[0];
            if (authorization == null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return null;
            bearerKey = authorization[7..];
            if (!ValidKey(bearerKey)) return null;
        }
        if (apiKey == null && bearerKey == null) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey ?? bearerKey!));
        if (apiKey != null && bearerKey != null &&
            !CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(Encoding.UTF8.GetBytes(bearerKey))))
            return null;

        DiscoveredCaller? match = null;
        Span<byte> expected = stackalloc byte[32];
        foreach (var caller in callers)
        {
            foreach (var configuredHash in caller.KeyHashes)
            {
                if (!configuredHash.StartsWith("sha256:", StringComparison.Ordinal) || configuredHash.Length != 71)
                    continue;
                if (Convert.FromHexString(configuredHash.AsSpan(7), expected, out var charsRead, out var bytesWritten)
                    == OperationStatus.Done && charsRead == 64 && bytesWritten == 32 && CryptographicOperations.FixedTimeEquals(hash, expected))
                    match ??= caller;
            }
        }
        return match;
    }

    public static string? ResolveZone(IHeaderDictionary headers, DiscoveredCaller caller)
    {
        if (!headers.TryGetValue("x-lb-data-zone", out var zones)) return caller.Zones.FirstOrDefault();
        if (zones.Count != 1) return null;
        var zone = zones[0];
        return zone != null && caller.Zones.Contains(zone, StringComparer.Ordinal) ? zone : null;
    }

    public static async Task<RequestInput?> ReadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength > MaximumBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return null;
        }
        var path = context.Request.Path.Value ?? "";
        if (path.Contains('\\') || path.Split('/').Any(segment => segment is "." or ".."))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return null;
        }
        var isV1 = path.StartsWith("/v1/", StringComparison.Ordinal) || path.StartsWith("/openai/v1/", StringComparison.Ordinal);
        string? model = null;
        if (!isV1 && path.StartsWith(DeploymentPrefix, StringComparison.Ordinal))
        {
            var segment = path[DeploymentPrefix.Length..].Split('/', 2)[0];
            if (segment.Length != 0) model = Uri.UnescapeDataString(segment);
        }
        if (!isV1 && string.IsNullOrWhiteSpace(model))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return null;
        }

        using var buffered = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var count = await context.Request.Body.ReadAsync(buffer.AsMemory(0,
                    Math.Min(buffer.Length, MaximumBodyBytes - (int)buffered.Length + 1)), cancellationToken);
                if (count == 0) break;
                if (buffered.Length + count > MaximumBodyBytes)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return null;
                }
                buffered.Write(buffer, 0, count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var body = buffered.ToArray();
        var streaming = false;
        try
        {
            if (body.Length != 0)
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.ValueKind != JsonValueKind.Object)
                {
                    if (isV1) model = null;
                }
                else
                {
                    streaming = json.RootElement.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True;
                    if (isV1)
                    {
                        model = json.RootElement.TryGetProperty("model", out var modelValue) && modelValue.ValueKind == JsonValueKind.String
                            ? modelValue.GetString() : null;
                        var names = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var property in json.RootElement.EnumerateObject())
                            if (!names.Add(property.Name)) model = null;
                    }
                }
            }
        }
        catch (JsonException)
        {
            if (isV1) model = null;
        }
        if (string.IsNullOrWhiteSpace(model))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return null;
        }
        return new RequestInput(model, streaming, body, path, isV1);
    }

    public byte[] BodyFor(Deployment deployment)
    {
        if (!v1) return Body;
        var json = JsonNode.Parse(Body)!.AsObject();
        json["model"] = deployment.DeploymentName;
        return JsonSerializer.SerializeToUtf8Bytes(json);
    }

    public PathString PathFor(Deployment deployment)
    {
        if (v1) return new PathString(originalPath.StartsWith("/v1/", StringComparison.Ordinal) ? "/openai" + originalPath : originalPath);
        var segmentEnd = originalPath.IndexOf('/', DeploymentPrefix.Length);
        var suffix = segmentEnd < 0 ? "" : originalPath[segmentEnd..];
        return PathString.FromUriComponent(DeploymentPrefix + Uri.EscapeDataString(deployment.DeploymentName))
            .Add(new PathString(suffix));
    }

    private static bool ValidKey(string? key)
    {
        if (key == null || key.Length != 47 || !key.StartsWith("lbk_", StringComparison.Ordinal)) return false;
        var encoded = key[4..];
        foreach (var character in encoded)
            if (!char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_') return false;
        Span<byte> decoded = stackalloc byte[32];
        return Convert.TryFromBase64String(encoded.Replace('-', '+').Replace('_', '/') + "=", decoded, out var written)
            && written == 32 && Convert.ToBase64String(decoded).TrimEnd('=').Replace('+', '-').Replace('/', '_') == encoded;
    }
}
