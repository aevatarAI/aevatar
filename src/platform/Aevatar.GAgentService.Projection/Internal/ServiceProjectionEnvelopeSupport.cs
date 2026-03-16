using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.Internal;

internal static class ServiceCommittedStateSupport
{
    public static bool TryGetObservedPayload(
        EventEnvelope envelope,
        IProjectionClock clock,
        out Any? payload,
        out string eventId,
        out long stateVersion,
        out DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(clock);

        if (CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out payload, out eventId, out stateVersion) &&
            payload != null)
        {
            observedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, clock.UtcNow);
            return true;
        }

        payload = envelope.Payload;
        eventId = envelope.Id ?? string.Empty;
        stateVersion = 0;
        observedAt = EventEnvelopeTimestampResolver.Resolve(envelope, clock.UtcNow);
        return payload != null;
    }

    public static long ResolveNextStateVersion(long currentVersion, long observedStateVersion)
    {
        if (observedStateVersion > 0)
            return observedStateVersion;

        return currentVersion <= 0 ? 1 : currentVersion + 1;
    }
}
