using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Core.Schedules;
using Aevatar.GAgentService.Projection.Orchestration;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class TeamAutomationOperationObservationSessionEventProjector
    : ProjectionSessionEventProjectorBase<
        TeamAutomationOperationObservationProjectionContext,
        TeamAutomationOperationCommittedOutcome>
{
    public TeamAutomationOperationObservationSessionEventProjector(
        IProjectionSessionEventHub<TeamAutomationOperationCommittedOutcome> sessionEventHub)
        : base(sessionEventHub)
    {
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<TeamAutomationOperationCommittedOutcome>>
        ResolveSessionEventEntries(
            TeamAutomationOperationObservationProjectionContext context,
            EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.IsNullOrWhiteSpace(context.SessionId) ||
            !CommittedStateEventEnvelope.TryGetObservedPayload(
                envelope,
                out var payload,
                out _,
                out _) ||
            payload?.Is(TeamAutomationOperationObservedEvent.Descriptor) != true)
        {
            return EmptyEntries;
        }

        var observed = payload.Unpack<TeamAutomationOperationObservedEvent>();
        if (!string.Equals(observed.OperationId, context.SessionId, StringComparison.Ordinal))
            return EmptyEntries;

        return
        [
            new ProjectionSessionEventEntry<TeamAutomationOperationCommittedOutcome>(
                context.RootActorId,
                context.SessionId,
                TeamAutomationOperationObservationSessionEventCodec.ToOutcome(observed)),
        ];
    }
}
