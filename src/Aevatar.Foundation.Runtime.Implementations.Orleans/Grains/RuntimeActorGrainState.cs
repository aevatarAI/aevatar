namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

[GenerateSerializer]
public sealed class RuntimeActorGrainState
{
    [Id(0)]
    public string AgentId { get; set; } = string.Empty;

    [Id(1)]
    public string? AgentTypeName { get; set; }

    [Id(2)]
    public string? ParentId { get; set; }

    [Id(3)]
    public List<string> Children { get; set; } = [];
}
