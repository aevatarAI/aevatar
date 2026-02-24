using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionReleaseService : IProjectionPortReleaseService<WorkflowExecutionRuntimeLease>
{
    private readonly IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> _lifecycle;
    private readonly IWorkflowProjectionReadModelUpdater _readModelUpdater;
    private readonly IProjectionOwnershipCoordinator _ownershipCoordinator;

    public WorkflowProjectionReleaseService(
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> lifecycle,
        IWorkflowProjectionReadModelUpdater readModelUpdater,
        IProjectionOwnershipCoordinator ownershipCoordinator)
    {
        _lifecycle = lifecycle;
        _readModelUpdater = readModelUpdater;
        _ownershipCoordinator = ownershipCoordinator;
    }

    public async Task ReleaseIfIdleAsync(
        WorkflowExecutionRuntimeLease runtimeLease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeLease);
        ct.ThrowIfCancellationRequested();

        if (runtimeLease.GetLiveSinkSubscriptionCount() > 0)
            return;

        var context = runtimeLease.Context;
        await _lifecycle.StopAsync(context, ct);
        await _readModelUpdater.MarkStoppedAsync(context.RootActorId, ct);
        await _ownershipCoordinator.ReleaseAsync(context.RootActorId, runtimeLease.CommandId, ct);
    }
}
