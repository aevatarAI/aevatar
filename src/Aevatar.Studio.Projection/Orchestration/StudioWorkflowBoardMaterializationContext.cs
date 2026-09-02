using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.Studio.Projection.Orchestration;

public sealed class StudioWorkflowBoardMaterializationContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}
