namespace Aevatar.Workflow.Application.Abstractions.Projections;

/// <summary>
/// Opaque lease for one active workflow projection session.
/// </summary>
public interface IWorkflowExecutionProjectionLease
{
    string ActorId { get; }

    string CommandId { get; }
}

/// <summary>
/// Lease contract for workflow projections that locally renew ownership and can transfer that duty.
/// </summary>
public interface IWorkflowExecutionProjectionOwnershipLease : IWorkflowExecutionProjectionLease
{
    ValueTask StopOwnershipHeartbeatAsync();
}
