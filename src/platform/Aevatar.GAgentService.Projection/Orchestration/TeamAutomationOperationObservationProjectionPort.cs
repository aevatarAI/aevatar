using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Projection.Configuration;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class TeamAutomationOperationObservationProjectionPort
    : EventSinkProjectionLifecyclePortBase<
        ITeamAutomationOperationObservationProjectionLease,
        TeamAutomationOperationObservationRuntimeLease,
        TeamAutomationOperationCommittedOutcome>,
      ITeamAutomationOperationObservationProjectionPort
{
    private readonly IProjectionScopeAttachExistingLeaseLookup<
        TeamAutomationOperationObservationRuntimeLease> _attachExistingLeaseLookup;

    public TeamAutomationOperationObservationProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeReleaseService<TeamAutomationOperationObservationRuntimeLease> releaseService,
        IProjectionSessionEventHub<TeamAutomationOperationCommittedOutcome> sessionEventHub,
        IProjectionScopeAttachExistingLeaseLookup<TeamAutomationOperationObservationRuntimeLease>
            attachExistingLeaseLookup)
        : base(() => options.Enabled, releaseService, sessionEventHub)
    {
        _attachExistingLeaseLookup = attachExistingLeaseLookup
            ?? throw new ArgumentNullException(nameof(attachExistingLeaseLookup));
    }

    public async Task<EventSinkProjectionAttachment<ITeamAutomationOperationObservationProjectionLease>?>
        AttachExistingOperationProjectionAsync(
            string actorId,
            string operationId,
            IEventSink<TeamAutomationOperationCommittedOutcome> sink,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled ||
            string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(operationId))
        {
            return null;
        }

        var lease = await _attachExistingLeaseLookup.TryGetAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId.Trim(),
                ProjectionKind = ServiceProjectionKinds.TeamAutomationOperationObservation,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = operationId.Trim(),
            },
            ct).ConfigureAwait(false);
        if (lease == null)
            return null;

        var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<ITeamAutomationOperationObservationProjectionLease>(
                lease,
                liveSinkLease);
    }
}
