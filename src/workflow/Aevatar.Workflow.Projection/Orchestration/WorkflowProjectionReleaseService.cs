using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionReleaseService
    : ProjectionReleaseServiceBase<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
{
    private readonly IWorkflowProjectionReadModelUpdater _readModelUpdater;
    private readonly IProjectionOwnershipCoordinator _ownershipCoordinator;

    public WorkflowProjectionReleaseService(
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> lifecycle,
        IWorkflowProjectionReadModelUpdater readModelUpdater,
        IProjectionOwnershipCoordinator ownershipCoordinator)
        : base(lifecycle)
    {
        _readModelUpdater = readModelUpdater;
        _ownershipCoordinator = ownershipCoordinator;
    }

    protected override WorkflowExecutionProjectionContext ResolveContext(WorkflowExecutionRuntimeLease runtimeLease) =>
        runtimeLease.Context;

    protected override async Task OnStoppedAsync(
        WorkflowExecutionRuntimeLease runtimeLease,
        WorkflowExecutionProjectionContext context,
        CancellationToken ct)
    {
        await _readModelUpdater.MarkStoppedAsync(context.RootActorId, ct);
        await _ownershipCoordinator.ReleaseAsync(context.RootActorId, runtimeLease.CommandId, ct);
    }
}
