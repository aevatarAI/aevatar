namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionMaterializationContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }
}
