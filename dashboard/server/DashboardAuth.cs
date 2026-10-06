using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace openai_loadbalancer.Dashboard.Server;

public sealed class DashboardServerOptions
{
    public bool DevAuth { get; set; }
    public string LbIdentityObjectId { get; set; } = "";
}

public static class DashboardAuth
{
    public static bool Allowed(HttpRequest request, DashboardServerOptions options, bool ingest)
    {
        if (options.DevAuth)
        {
            if (!ingest)
                return true;
            return AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header) &&
                header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) && header.Parameter != null &&
                CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header.Parameter), Encoding.UTF8.GetBytes("devkit-token"));
        }
        if (ingest && (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var bearer) ||
            !bearer.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(bearer.Parameter)))
            return false;
        var encoded = request.Headers["X-MS-CLIENT-PRINCIPAL"];
        if (encoded.Count != 1 || encoded[0] is not { Length: > 0 and <= 32768 } value)
            return false;
        try
        {
            using var json = JsonDocument.Parse(Convert.FromBase64String(value));
            var principal = json.RootElement;
            if (!principal.TryGetProperty("auth_typ", out var auth) || auth.GetString() != "aad" ||
                !principal.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Array)
                return false;
            var ids = claims.EnumerateArray().Where(claim => claim.TryGetProperty("typ", out var type) &&
                type.GetString() is "oid" or "http://schemas.microsoft.com/identity/claims/objectidentifier")
                .Select(claim => claim.GetProperty("val").GetString()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return ids.Length == 1 && Guid.TryParse(ids[0], out var id) &&
                (!ingest || Guid.TryParse(options.LbIdentityObjectId, out var lbId) && id == lbId);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }
}
