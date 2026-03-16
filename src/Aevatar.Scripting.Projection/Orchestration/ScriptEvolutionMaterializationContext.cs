namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionMaterializationContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }

    public static implicit operator ScriptEvolutionMaterializationContext(
        ScriptEvolutionSessionProjectionContext context) =>
        new()
        {
            RootActorId = context.RootActorId,
            ProjectionKind = context.ProjectionKind,
        };
}
