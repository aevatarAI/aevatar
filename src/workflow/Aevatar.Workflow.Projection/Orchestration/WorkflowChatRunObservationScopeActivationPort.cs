using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowChatRunObservationScopeLeasePreparationPort
    : IWorkflowChatRunObservationScopeLeasePreparationPort
{
    private readonly IProjectionScopeActivationService<WorkflowExecutionRuntimeLease> _activationService;
    private readonly IProjectionScopeReleaseService<WorkflowExecutionRuntimeLease> _releaseService;

    public WorkflowChatRunObservationScopeLeasePreparationPort(
        IProjectionScopeActivationService<WorkflowExecutionRuntimeLease> activationService,
        IProjectionScopeReleaseService<WorkflowExecutionRuntimeLease> releaseService)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
    }

    public async Task<WorkflowChatRunObservationScopeLeasePreparation?> PrepareAsync(
        string actorId,
        string commandId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(commandId))
            return null;

        try
        {
            var normalizedActorId = actorId.Trim();
            var normalizedCommandId = commandId.Trim();
            _ = await _activationService.EnsureAsync(
                new ProjectionScopeStartRequest
                {
                    RootActorId = normalizedActorId,
                    ProjectionKind = WorkflowProjectionKinds.ExecutionSession,
                    Mode = ProjectionRuntimeMode.SessionObservation,
                    SessionId = normalizedCommandId,
                },
                ct).ConfigureAwait(false);

            return new WorkflowChatRunObservationScopeLeasePreparation(
                normalizedActorId,
                normalizedCommandId);
        }
        catch
        {
            return null;
        }
    }

    public Task ReleaseAsync(
        WorkflowChatRunObservationScopeLeasePreparation preparation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ct.ThrowIfCancellationRequested();

        var lease = new WorkflowExecutionRuntimeLease(new WorkflowExecutionProjectionContext
        {
            RootActorId = preparation.ActorId,
            ProjectionKind = WorkflowProjectionKinds.ExecutionSession,
            SessionId = preparation.CommandId,
        });
        return _releaseService.ReleaseIfIdleAsync(lease, ct);
    }
}
