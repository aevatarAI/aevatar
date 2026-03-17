namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowBindingProjectionContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}
