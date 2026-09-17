using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowDefinitionBindObservationScopeLeasePreparationPort
    : IWorkflowDefinitionBindObservationScopeLeasePreparationPort
{
    private readonly IProjectionScopeActivationService<WorkflowDefinitionBindObservationRuntimeLease>
        _activationService;
    private readonly IProjectionScopeReleaseService<WorkflowDefinitionBindObservationRuntimeLease>
        _releaseService;

    public WorkflowDefinitionBindObservationScopeLeasePreparationPort(
        IProjectionScopeActivationService<WorkflowDefinitionBindObservationRuntimeLease> activationService,
        IProjectionScopeReleaseService<WorkflowDefinitionBindObservationRuntimeLease> releaseService)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
    }

    public async Task<WorkflowDefinitionBindObservationScopeLeasePreparation?> PrepareAsync(
        string actorId,
        string commandId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(commandId))
            return null;

        var normalizedActorId = actorId.Trim();
        var normalizedCommandId = commandId.Trim();
        var lease = await _activationService.EnsureAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = normalizedActorId,
                ProjectionKind = WorkflowProjectionKinds.DefinitionBindObservation,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = normalizedCommandId,
            },
            ct).ConfigureAwait(false);
        if (lease == null)
        {
            return null;
        }

        return new WorkflowDefinitionBindObservationScopeLeasePreparation(
            normalizedActorId,
            normalizedCommandId);
    }

    public Task ReleaseAsync(
        WorkflowDefinitionBindObservationScopeLeasePreparation preparation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ct.ThrowIfCancellationRequested();

        return _releaseService.ReleaseIfIdleAsync(
            new WorkflowDefinitionBindObservationRuntimeLease(
                new WorkflowDefinitionBindObservationProjectionContext
                {
                    RootActorId = preparation.ActorId,
                    ProjectionKind = WorkflowProjectionKinds.DefinitionBindObservation,
                    SessionId = preparation.CommandId,
                }),
            ct);
    }
}
