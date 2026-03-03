
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionActivationService : IProjectionPortActivationService<WorkflowExecutionRuntimeLease>
{
    private readonly IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> _lifecycle;
    private readonly IProjectionClock _clock;
    private readonly IWorkflowExecutionProjectionContextFactory _contextFactory;
    private readonly IProjectionOwnershipCoordinator _ownershipCoordinator;
    private readonly IWorkflowProjectionReadModelUpdater _readModelUpdater;

    public WorkflowProjectionActivationService(
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> lifecycle,
        IProjectionClock clock,
        IWorkflowExecutionProjectionContextFactory contextFactory,
        IProjectionOwnershipCoordinator ownershipCoordinator,
        IWorkflowProjectionReadModelUpdater readModelUpdater)
    {
        _lifecycle = lifecycle;
        _clock = clock;
        _contextFactory = contextFactory;
        _ownershipCoordinator = ownershipCoordinator;
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

        await _ownershipCoordinator.AcquireAsync(rootActorId, commandId, ct);
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
            await _ownershipCoordinator.ReleaseAsync(rootActorId, commandId, CancellationToken.None);
        }
        catch
        {
            // Best effort cleanup: ownership may already be released or unavailable.
        }
    }
}
