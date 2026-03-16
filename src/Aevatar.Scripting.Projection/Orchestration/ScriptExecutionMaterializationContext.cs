namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptExecutionMaterializationContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }

    public static implicit operator ScriptExecutionMaterializationContext(
        ScriptExecutionProjectionContext context) =>
        new()
        {
            RootActorId = context.RootActorId,
            ProjectionKind = context.ProjectionKind,
        };
}
