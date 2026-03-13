using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using System.Runtime.ExceptionServices;

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
        Exception? firstException = null;

        try
        {
            await runtimeLease.StopProjectionReleaseListenerAsync();
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }

        try
        {
            await runtimeLease.StopOwnershipHeartbeatAsync();
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }

        try
        {
            await _readModelUpdater.MarkStoppedAsync(context.RootActorId, ct);
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }
        finally
        {
            try
            {
                await _ownershipCoordinator.ReleaseAsync(context.RootActorId, runtimeLease.CommandId, ct);
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }

        if (firstException != null)
            ExceptionDispatchInfo.Capture(firstException).Throw();
    }
}
