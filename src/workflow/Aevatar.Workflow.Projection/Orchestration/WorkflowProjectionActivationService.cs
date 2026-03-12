using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionActivationService
    : ProjectionActivationServiceBase<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
{
    private readonly IProjectionClock _clock;
    private readonly IWorkflowExecutionProjectionContextFactory _contextFactory;
    private readonly IProjectionOwnershipCoordinator _ownershipCoordinator;
    private readonly ProjectionOwnershipCoordinatorOptions _ownershipOptions;
    private readonly IWorkflowProjectionReadModelUpdater _readModelUpdater;

    public WorkflowProjectionActivationService(
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> lifecycle,
        IProjectionClock clock,
        IWorkflowExecutionProjectionContextFactory contextFactory,
        IProjectionOwnershipCoordinator ownershipCoordinator,
        IWorkflowProjectionReadModelUpdater readModelUpdater,
        ProjectionOwnershipCoordinatorOptions? ownershipOptions = null)
        : base(lifecycle)
    {
        _clock = clock;
        _contextFactory = contextFactory;
        _ownershipCoordinator = ownershipCoordinator;
        _ownershipOptions = ownershipOptions ?? new ProjectionOwnershipCoordinatorOptions();
        _readModelUpdater = readModelUpdater;
    }

    protected override async Task AcquireBeforeStartAsync(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct)
    {
        _ = projectionName;
        _ = input;
        await _ownershipCoordinator.AcquireAsync(rootEntityId, commandId, ct);
    }

    protected override WorkflowExecutionProjectionContext CreateContext(
        string rootEntityId,
        string workflowName,
        string input,
        string commandId,
        CancellationToken ct)
    {
        _ = ct;
        var startedAt = _clock.UtcNow;
        return _contextFactory.Create(
            rootEntityId,
            commandId,
            rootEntityId,
            workflowName,
            input,
            startedAt);
    }

    protected override async Task OnStartedAsync(
        string rootEntityId,
        string commandId,
        WorkflowExecutionProjectionContext context,
        CancellationToken ct)
    {
        _ = commandId;
        await _readModelUpdater.RefreshMetadataAsync(rootEntityId, context, ct);
    }

    protected override WorkflowExecutionRuntimeLease CreateRuntimeLease(WorkflowExecutionProjectionContext context) =>
        new(context, _ownershipCoordinator, _ownershipOptions);

    protected override async Task CleanupOnStartFailureAsync(
        string rootEntityId,
        string commandId)
    {
        await TryReleaseProjectionOwnershipAsync(rootEntityId, commandId);
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
