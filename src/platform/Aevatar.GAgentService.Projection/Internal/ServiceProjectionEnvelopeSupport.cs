using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.Internal;

internal static class ServiceCommittedStateSupport
{
    // refactor helper, no behavior change: shared committed state_root extraction for service projectors.
    public static bool TryGetObservedState<TState>(
        EventEnvelope envelope,
        IProjectionClock clock,
        out TState? state,
        out string eventId,
        out long stateVersion,
        out DateTimeOffset observedAt)
        where TState : class, IMessage<TState>, new()
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(clock);

        state = null;
        eventId = string.Empty;
        stateVersion = 0;
        observedAt = default;

        if (!CommittedStateEventEnvelope.TryUnpackState<TState>(
                envelope,
                out _,
                out var stateEvent,
                out state) ||
            stateEvent == null ||
            state == null ||
            stateEvent.Version <= 0)
        {
            return false;
        }

        eventId = stateEvent.EventId ?? string.Empty;
        stateVersion = stateEvent.Version;
        observedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, clock.UtcNow);
        return true;
    }

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
