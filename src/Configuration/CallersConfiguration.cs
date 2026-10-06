namespace openai_loadbalancer.Configuration;

public sealed class CallersConfiguration
{
    public List<Caller> Callers { get; init; } = new();
}

public sealed class Caller
{
    public required string Name { get; init; }
    public required List<string> Zones { get; init; }
    public required List<string> KeyHashes { get; init; }
}
