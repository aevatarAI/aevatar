using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection;

public sealed class WorkflowExecutionMaterializationContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }

    public ProjectionMaterializationRouteFingerprint? MaterializationRoute { get; set; }
}
