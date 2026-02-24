using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionActivationService : IWorkflowProjectionActivationService
{
    private readonly IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> _lifecycle;
    private readonly IProjectionClock _clock;
    private readonly IWorkflowExecutionProjectionContextFactory _contextFactory;
    private readonly IWorkflowProjectionLeaseManager _leaseManager;
    private readonly IWorkflowProjectionReadModelUpdater _readModelUpdater;

    public WorkflowProjectionActivationService(
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> lifecycle,
        IProjectionClock clock,
        IWorkflowExecutionProjectionContextFactory contextFactory,
        IWorkflowProjectionLeaseManager leaseManager,
        IWorkflowProjectionReadModelUpdater readModelUpdater)
    {
        _lifecycle = lifecycle;
        _clock = clock;
        _contextFactory = contextFactory;
        _leaseManager = leaseManager;
        _readModelUpdater = readModelUpdater;
    }

    public async Task<WorkflowExecutionRuntimeLease> EnsureAsync(
        string rootActorId,
        string workflowName,
        string input,
        string commandId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootActorId);

        await _leaseManager.AcquireAsync(rootActorId, commandId, ct);
        try
        {
            var startedAt = _clock.UtcNow;
            var context = _contextFactory.Create(
                rootActorId,
                commandId,
                rootActorId,
                workflowName,
                input,
                startedAt);

            await _lifecycle.StartAsync(context, ct);
            await _readModelUpdater.RefreshMetadataAsync(rootActorId, context, ct);
            return new WorkflowExecutionRuntimeLease(context);
        }
        catch
        {
            await TryReleaseProjectionOwnershipAsync(rootActorId, commandId);
            throw;
        }
    }

    private async Task TryReleaseProjectionOwnershipAsync(
        string rootActorId,
        string commandId)
    {
        try
        {
            await _leaseManager.ReleaseAsync(rootActorId, commandId, CancellationToken.None);
        }
        catch
        {
            // Best effort cleanup: ownership may already be released or unavailable.
        }
    }
}
