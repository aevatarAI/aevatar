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

        payload = null;
        eventId = string.Empty;
        stateVersion = 0;
        observedAt = default;

        if (!CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out payload, out eventId, out stateVersion) ||
            payload == null ||
            stateVersion <= 0)
        {
            return false;
        }

        observedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, clock.UtcNow);
        return true;
    }

    public static long ResolveNextStateVersion(long currentVersion, long observedStateVersion)
    {
        _ = currentVersion;
        return observedStateVersion;
    }
}
