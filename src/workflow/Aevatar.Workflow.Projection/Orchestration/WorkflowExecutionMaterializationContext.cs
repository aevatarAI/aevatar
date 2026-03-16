namespace Aevatar.Workflow.Projection;

public sealed class WorkflowExecutionMaterializationContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }

    public static implicit operator WorkflowExecutionMaterializationContext(
        WorkflowExecutionProjectionContext context) =>
        new()
        {
            RootActorId = context.RootActorId,
            ProjectionKind = context.ProjectionKind,
        };
}
