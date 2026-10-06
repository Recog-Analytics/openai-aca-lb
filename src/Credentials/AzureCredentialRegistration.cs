using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace openai_loadbalancer.Credentials;

public sealed class AzureCredentialOptions
{
    public string Credential { get; init; } = "ManagedIdentity";
}

public static class AzureCredentialRegistration
{
    public static IServiceCollection AddAzureCredential(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AzureCredentialOptions>().Bind(configuration.GetSection("Azure"))
            .Validate(options => options.Credential is "ManagedIdentity" or "Fake",
                "Azure:Credential must be ManagedIdentity or Fake.")
            .Validate<IHostEnvironment>((options, environment) =>
                options.Credential != "Fake" || environment.IsDevelopment(),
                "Azure:Credential Fake is allowed only in Development.")
            .ValidateOnStart();
        services.AddSingleton<TokenCredential>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AzureCredentialOptions>>().Value;
            if (options.Credential == "Fake")
                return new FakeTokenCredential();

            var clientId = configuration["AZURE_CLIENT_ID"];
            var identity = string.IsNullOrWhiteSpace(clientId) ? ManagedIdentityId.SystemAssigned
                : ManagedIdentityId.FromUserAssignedClientId(clientId);
            return new ManagedIdentityCredential(identity);
        });
        return services;
    }
}
