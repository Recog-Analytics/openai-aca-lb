using System.Collections.Frozen;

namespace openai_loadbalancer.Routing;

public sealed record ModelResolution(ModelKey? Key, string? Error, IReadOnlyList<string> AvailableVersions)
{
    public bool Success => Key.HasValue;
}

public interface IModelResolver
{
    ModelResolution ResolveModel(string requestedModel);
}

public sealed class RoutingTable : IModelResolver
{
    private readonly FrozenDictionary<ModelKey, IReadOnlyList<Deployment>> byModel;
    private readonly FrozenDictionary<string, string> defaultVersions;

    public IReadOnlyList<Deployment> Deployments { get; }

    internal RoutingTable(IEnumerable<Deployment> deployments, IReadOnlyDictionary<string, string> defaultVersions)
    {
        Deployments = Array.AsReadOnly(deployments.ToArray());
        byModel = Deployments.GroupBy(deployment => deployment.Model)
            .ToFrozenDictionary(group => group.Key, group => (IReadOnlyList<Deployment>)Array.AsReadOnly(group.ToArray()));
        this.defaultVersions = defaultVersions.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public IReadOnlyList<Deployment> GetDeployments(ModelKey model) =>
        byModel.TryGetValue(model, out var deployments) ? deployments : Array.Empty<Deployment>();

    public ModelResolution ResolveModel(string requestedModel)
    {
        var parts = requestedModel.Split('@');
        if (parts.Length > 2 || parts.Any(string.IsNullOrWhiteSpace))
            return new(null, "Specify a model as 'name' or 'name@version'.", Array.Empty<string>());

        var name = parts[0];
        var versions = Array.AsReadOnly(byModel.Keys.Where(key => key.Name == name)
            .Select(key => key.Version).Order(StringComparer.Ordinal).ToArray());
        if (versions.Count == 0)
            return new(null, $"Unknown model '{name}'.", versions);

        var version = parts.Length == 2 ? parts[1] : defaultVersions.GetValueOrDefault(name);
        if (version == null)
        {
            if (versions.Count != 1)
                return new(null, $"Model '{name}' requires a version. Available versions: {string.Join(", ", versions)}.", versions);
            version = versions[0];
        }
        var model = new ModelKey(name, version);
        return byModel.ContainsKey(model)
            ? new(model, null, versions)
            : new(null, $"Version '{version}' is unavailable for model '{name}'. Available versions: {string.Join(", ", versions)}.", versions);
    }
}
