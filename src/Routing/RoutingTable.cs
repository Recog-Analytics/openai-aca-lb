using System.Collections.Frozen;

namespace openai_loadbalancer.Routing;

public enum PoolKind { Model, Deployment }

/// <summary>The deployments a request can use: one model key, or every deployment with one name.</summary>
public readonly record struct RoutingPool(PoolKind Kind, string Name)
{
    public static RoutingPool For(ModelKey model) => new(PoolKind.Model, model.ToString());
    public string KindName => Kind == PoolKind.Model ? "model" : "deployment";
    public override string ToString() => Name;
}

/// <summary>Key is the pool's model key, or null for a deployment-name pool that spans several model keys.</summary>
public sealed record ModelResolution(RoutingPool? Pool, ModelKey? Key, string? Error, IReadOnlyList<string> AvailableVersions)
{
    public bool Success => Pool.HasValue;
}

public interface IModelResolver
{
    ModelResolution ResolveModel(string requestedModel);
}

public sealed class RoutingTable : IModelResolver
{
    private readonly FrozenDictionary<ModelKey, IReadOnlyList<Deployment>> byModel;
    private readonly FrozenDictionary<string, IReadOnlyList<Deployment>> byName;
    private readonly FrozenDictionary<string, string> defaultVersions;
    private readonly FrozenDictionary<string, string> aliases;

    public IReadOnlyList<Deployment> Deployments { get; }

    internal RoutingTable(IEnumerable<Deployment> deployments, IReadOnlyDictionary<string, string> defaultVersions,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        Deployments = Array.AsReadOnly(deployments.ToArray());
        byModel = Deployments.GroupBy(deployment => deployment.Model)
            .ToFrozenDictionary(group => group.Key, group => (IReadOnlyList<Deployment>)Array.AsReadOnly(group.ToArray()));
        byName = Deployments.GroupBy(deployment => deployment.DeploymentName, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(group => group.Key, group => (IReadOnlyList<Deployment>)Array.AsReadOnly(group.ToArray()),
                StringComparer.OrdinalIgnoreCase);
        this.defaultVersions = defaultVersions.ToFrozenDictionary(StringComparer.Ordinal);
        this.aliases = (aliases ?? new Dictionary<string, string>()).ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Deployment> GetDeployments(ModelKey model) =>
        byModel.TryGetValue(model, out var deployments) ? deployments : Array.Empty<Deployment>();

    public IReadOnlyList<Deployment> GetDeployments(RoutingPool pool) => pool.Kind == PoolKind.Model
        ? GetDeployments(ModelKey.Parse(pool.Name))
        : byName.TryGetValue(pool.Name, out var deployments) ? deployments : Array.Empty<Deployment>();

    // Order: model key, explicit alias, deployment name. The builder rejects aliases that equal a model name.
    public ModelResolution ResolveModel(string requestedModel)
    {
        if (ResolveModelKey(requestedModel) is { } model)
            return model;
        if (aliases.TryGetValue(requestedModel, out var target))
            return ResolveModelKey(target) ?? ResolveDeploymentName(target) ?? Unknown(target);
        return ResolveDeploymentName(requestedModel) ?? Unknown(requestedModel);
    }

    private ModelResolution? ResolveModelKey(string requestedModel)
    {
        var parts = requestedModel.Split('@');
        if (parts.Length > 2 || parts.Any(string.IsNullOrWhiteSpace))
            return new(null, null, "Specify a model as 'name' or 'name@version'.", Array.Empty<string>());

        var name = parts[0];
        var versions = Array.AsReadOnly(byModel.Keys.Where(key => key.Name == name)
            .Select(key => key.Version).Order(StringComparer.Ordinal).ToArray());
        if (versions.Count == 0)
            return null;

        var version = parts.Length == 2 ? parts[1] : defaultVersions.GetValueOrDefault(name);
        if (version == null)
        {
            if (versions.Count != 1)
                return new(null, null, $"Model '{name}' requires a version. Available versions: {string.Join(", ", versions)}.", versions);
            version = versions[0];
        }
        var model = new ModelKey(name, version);
        return byModel.ContainsKey(model)
            ? new(RoutingPool.For(model), model, null, versions)
            : new(null, null, $"Version '{version}' is unavailable for model '{name}'. Available versions: {string.Join(", ", versions)}.", versions);
    }

    private ModelResolution? ResolveDeploymentName(string name)
    {
        if (!byName.TryGetValue(name, out var deployments))
            return null;
        var models = deployments.Select(deployment => deployment.Model).Distinct().ToArray();
        return new(new RoutingPool(PoolKind.Deployment, deployments[0].DeploymentName), models.Length == 1 ? models[0] : null,
            null, Array.Empty<string>());
    }

    private static ModelResolution Unknown(string requestedModel) =>
        new(null, null, $"Unknown model or deployment '{requestedModel.Split('@')[0]}'.", Array.Empty<string>());
}
