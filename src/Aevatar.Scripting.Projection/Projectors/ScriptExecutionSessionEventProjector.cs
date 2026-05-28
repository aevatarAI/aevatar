using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptExecutionSessionEventProjector
    : ProjectionSessionEventProjectorBase<ScriptExecutionProjectionContext, EventEnvelope>
{
    public ScriptExecutionSessionEventProjector(
        IProjectionSessionEventHub<EventEnvelope> sessionEventHub)
        : base(sessionEventHub)
    {
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<EventEnvelope>> ResolveSessionEventEntries(
        ScriptExecutionProjectionContext context,
        EventEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(context.RootActorId) || string.IsNullOrWhiteSpace(context.SessionId))
            return EmptyEntries;

        var correlationId = ResolveSessionCorrelationId(envelope);
        if (!IsLegacyActorScopedSession(context) &&
            !string.Equals(correlationId, context.SessionId, StringComparison.Ordinal))
        {
            return EmptyEntries;
        }

        return
        [
            new ProjectionSessionEventEntry<EventEnvelope>(
                context.RootActorId,
                context.SessionId,
                envelope)
        ];
    }

    private static bool IsLegacyActorScopedSession(ScriptExecutionProjectionContext context) =>
        string.Equals(context.RootActorId, context.SessionId, StringComparison.Ordinal);

    private static string ResolveSessionCorrelationId(EventEnvelope envelope)
    {
        if (!string.IsNullOrWhiteSpace(envelope.Propagation?.CorrelationId))
            return envelope.Propagation.CorrelationId;

        // Refactor (iter149/cluster-1133): Old pattern: envelope-based session routing.  New principle: typed ScriptRunOutcomeRecordedEvent.CorrelationId fallback for session correlation.
        if (envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) == true)
        {
            var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
            var eventData = published.StateEvent?.EventData;
            if (eventData?.Is(ScriptRunOutcomeRecordedEvent.Descriptor) == true)
                return eventData.Unpack<ScriptRunOutcomeRecordedEvent>().CorrelationId ?? string.Empty;
        }

        return string.Empty;
    }
}
