namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

[GenerateSerializer]
public sealed class RuntimeActorStateSnapshot
{
    [Id(0)]
    public string? AgentTypeName { get; set; }

    [Id(1)]
    public string? StateTypeName { get; set; }

    [Id(2)]
    public byte[]? StateBytes { get; set; }

    [Id(3)]
    public long StateVersion { get; set; }
}
