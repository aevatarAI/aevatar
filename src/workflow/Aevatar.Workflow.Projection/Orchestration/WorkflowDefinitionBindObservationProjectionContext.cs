using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowDefinitionBindObservationProjectionContext : IProjectionSessionContext
{
    public required string SessionId { get; init; }

    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}
