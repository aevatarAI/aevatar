using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Core.Schedules.Authorization;
using Aevatar.GAgentService.Projection.Orchestration;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class NyxIdAuthorizationCatalogRefreshObservationSessionEventProjector
    : ProjectionSessionEventProjectorBase<
        NyxIdAuthorizationCatalogRefreshObservationProjectionContext,
        NyxIdAuthorizationCatalogRefreshCommittedOutcome>
{
    public NyxIdAuthorizationCatalogRefreshObservationSessionEventProjector(
        IProjectionSessionEventHub<NyxIdAuthorizationCatalogRefreshCommittedOutcome> sessionEventHub)
        : base(sessionEventHub)
    {
    }

    protected override IReadOnlyList<
        ProjectionSessionEventEntry<NyxIdAuthorizationCatalogRefreshCommittedOutcome>>
        ResolveSessionEventEntries(
            NyxIdAuthorizationCatalogRefreshObservationProjectionContext context,
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
            payload?.Is(NyxIdAuthorizationCatalogRefreshOutcomeEvent.Descriptor) != true)
        {
            return EmptyEntries;
        }

        var observed = payload.Unpack<NyxIdAuthorizationCatalogRefreshOutcomeEvent>();
        if (!string.Equals(observed.RefreshId, context.SessionId, StringComparison.Ordinal))
            return EmptyEntries;

        return
        [
            new ProjectionSessionEventEntry<NyxIdAuthorizationCatalogRefreshCommittedOutcome>(
                context.RootActorId,
                context.SessionId,
                NyxIdAuthorizationCatalogRefreshObservationSessionEventCodec.ToOutcome(observed)),
        ];
    }
}
