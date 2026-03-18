namespace Aevatar.Workflow.Projection;

/// <summary>
/// Actor-scoped projection context for CQRS read model updates.
/// </summary>
public sealed class WorkflowExecutionProjectionContext
    : IProjectionSessionContext
{
    public required string SessionId { get; init; }
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }
}
