using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionLeaseManager : IWorkflowProjectionLeaseManager
{
    private readonly IProjectionOwnershipCoordinator _ownershipCoordinator;

    public WorkflowProjectionLeaseManager(IProjectionOwnershipCoordinator ownershipCoordinator)
    {
        _ownershipCoordinator = ownershipCoordinator;
    }

    public Task AcquireAsync(
        string rootActorId,
        string commandId,
        CancellationToken ct = default) =>
        _ownershipCoordinator.AcquireAsync(rootActorId, commandId, ct);

    public Task ReleaseAsync(
        string rootActorId,
        string commandId,
        CancellationToken ct = default) =>
        _ownershipCoordinator.ReleaseAsync(rootActorId, commandId, ct);
}
