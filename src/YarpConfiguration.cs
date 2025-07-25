using System.Net;
using Azure.Core;
using Azure.Identity;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;
using Yarp.ReverseProxy.Transforms;

namespace openai_loadbalancer;

public class YarpConfiguration
{
    private readonly IReadOnlyDictionary<string, BackendConfig> backends;

    public YarpConfiguration(IReadOnlyDictionary<string, BackendConfig> backends)
    {
        this.backends = backends;
    }

    public RouteConfig[] GetRoutes()
    {
        return
        [
                new RouteConfig()
                {
                    RouteId = "route",
                    ClusterId = "cluster",
                    Match = new RouteMatch
                    {
                        Path = "{**catch-all}"
                    }
                }
            ];
    }
    public ClusterConfig[] GetClusters()
    {

        var destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var backend in backends)
        {
            var metadata = new Dictionary<string, string>
            {
                { "url", backend.Value.Url },
                { "priority", backend.Value.Priority.ToString() }
            };
            destinations.Add(backend.Key, new DestinationConfig { Address = backend.Value.Url, Metadata = metadata });
        }

        return
        [
                new ClusterConfig()
                {
                    ClusterId = "cluster",
                    HealthCheck = new HealthCheckConfig
                    {
                        Passive = new PassiveHealthCheckConfig
                        {
                            Enabled = true,
                            Policy = ThrottlingHealthPolicy.ThrottlingPolicyName,
                        }
                    },
                    Destinations = destinations,
                    HttpRequest = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(BackendConfig.HttpTimeoutSeconds) }
                }
        ];
    }

    public Func<ResponseTransformContext, ValueTask> TransformResponse()
    {
        return context =>
        {
            if (context.ProxyResponse?.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError)
            {
                var reverseProxyContext = context.HttpContext.GetReverseProxyFeature();

                var canRetry = reverseProxyContext.AllDestinations.Count(m => m.Health.Passive != DestinationHealth.Unhealthy && m.DestinationId != reverseProxyContext?.ProxiedDestination?.DestinationId) > 0;

                if (canRetry)
                {
                    // Suppress the response body from being written when we will retry
                    context.SuppressResponseBody = true;
                }
            }

            return default;
        };
    }

    internal Func<RequestTransformContext, ValueTask> TransformRequest()
    {
        return async context =>
        {
            var proxyHeaders = context.ProxyRequest.Headers;
            var reverseProxyFeature = context.HttpContext.GetReverseProxyFeature();

            var backendConfig = backends[reverseProxyFeature.AvailableDestinations[0].DestinationId];

            // Check if client provided an Authorization header (Azure AD token)
            var clientAuthHeader = context.HttpContext.Request.Headers.Authorization.FirstOrDefault();
            var hasClientToken = !string.IsNullOrEmpty(clientAuthHeader) && clientAuthHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

            if (hasClientToken)
            {
                // Forward the client's Azure AD token
                proxyHeaders.Remove("Authorization");
                proxyHeaders.Add("Authorization", clientAuthHeader);
            }
            else if (!string.IsNullOrEmpty(backendConfig.ApiKey))
            {
                // Use configured API key if no client token provided
                proxyHeaders.Remove("api-key");
                proxyHeaders.Add("api-key", backendConfig.ApiKey);
            }
            else
            {
                // Fallback to DefaultAzureCredential if no client token and no API key
                AccessToken accessToken = await new DefaultAzureCredential().GetTokenAsync(new TokenRequestContext(scopes: ["https://cognitiveservices.azure.com/.default"]));
                proxyHeaders.Remove("Authorization");
                proxyHeaders.Add("Authorization", "Bearer " + accessToken.Token);
            }

            if (backendConfig.DeploymentName != null)
            {
                var pathSegments = context.Path.Value!.Split('/');

                if (pathSegments.Length >= 4)
                {
                    //Incoming path should be coming in format "/openai/deployments/{deploymentName}/*"
                    //We must grab the {deploynameName} from in the incoming request (array position [3]) and replace it by the one specified in the configuration
                    context.Path = new PathString(context.Path.Value.Replace(pathSegments[3], backendConfig.DeploymentName));
                }
            }
        };
    }
}

public class ThrottlingHealthPolicy : IPassiveHealthCheckPolicy
{
    public static string ThrottlingPolicyName = "ThrottlingPolicy";
    private readonly IDestinationHealthUpdater _healthUpdater;

    public ThrottlingHealthPolicy(IDestinationHealthUpdater healthUpdater)
    {
        _healthUpdater = healthUpdater;
    }

    public string Name => ThrottlingPolicyName;

    public void RequestProxied(HttpContext context, ClusterState cluster, DestinationState destination)
    {
        var headers = context.Response.Headers;

        if (context.Response.StatusCode is 429 or >= 500)
        {
            var retryAfterSeconds = 10;

            if (headers.TryGetValue("Retry-After", out var retryAfterHeader) && retryAfterHeader.Count > 0 && int.TryParse(retryAfterHeader[0], out var retryAfter))
            {
                retryAfterSeconds = retryAfter;
            }
            else
            if (headers.TryGetValue("x-ratelimit-reset-requests", out var ratelimiResetRequests) && ratelimiResetRequests.Count > 0 && int.TryParse(ratelimiResetRequests[0], out var ratelimiResetRequest))
            {
                retryAfterSeconds = ratelimiResetRequest;
            }
            else
            if (headers.TryGetValue("x-ratelimit-reset-tokens", out var ratelimitResetTokens) && ratelimitResetTokens.Count > 0 && int.TryParse(ratelimitResetTokens[0], out var ratelimitResetToken))
            {
                retryAfterSeconds = ratelimitResetToken;
            }

            _healthUpdater.SetPassive(cluster, destination, DestinationHealth.Unhealthy, TimeSpan.FromSeconds(retryAfterSeconds));
        }
    }
}
