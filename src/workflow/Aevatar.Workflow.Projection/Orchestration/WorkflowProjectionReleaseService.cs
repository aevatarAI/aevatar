using Aevatar.CQRS.Projection.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionReleaseService : IWorkflowProjectionReleaseService
{
    private readonly IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> _lifecycle;
    private readonly IWorkflowProjectionSinkSubscriptionManager _sinkSubscriptionManager;
    private readonly IWorkflowProjectionReadModelUpdater _readModelUpdater;
    private readonly IWorkflowProjectionLeaseManager _leaseManager;

    public WorkflowProjectionReleaseService(
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> lifecycle,
        IWorkflowProjectionSinkSubscriptionManager sinkSubscriptionManager,
        IWorkflowProjectionReadModelUpdater readModelUpdater,
        IWorkflowProjectionLeaseManager leaseManager)
    {
        _lifecycle = lifecycle;
        _sinkSubscriptionManager = sinkSubscriptionManager;
        _readModelUpdater = readModelUpdater;
        _leaseManager = leaseManager;
    }

    public async Task ReleaseIfIdleAsync(
        WorkflowExecutionRuntimeLease runtimeLease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeLease);
        ct.ThrowIfCancellationRequested();

        if (_sinkSubscriptionManager.GetSubscriptionCount(runtimeLease) > 0)
            return;

        var context = runtimeLease.Context;
        await _lifecycle.StopAsync(context, ct);
        await _readModelUpdater.MarkStoppedAsync(context.RootActorId, ct);
        await _leaseManager.ReleaseAsync(context.RootActorId, runtimeLease.CommandId, ct);
    }
}
