using Aevatar.Foundation.Abstractions;
using Google.Protobuf;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public static class CommittedStateEventEnvelope
{
    public static bool TryUnpack(
        EventEnvelope envelope,
        out CommittedStateEventPublished? published)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        published = null;
        if (envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) != true)
            return false;

        published = envelope.Payload.Unpack<CommittedStateEventPublished>();
        return published.StateEvent != null;
    }

    public static bool TryUnpackState<TState>(
        EventEnvelope envelope,
        out CommittedStateEventPublished? published,
        out StateEvent? stateEvent,
        out TState? state)
        where TState : class, IMessage<TState>, new()
    {
        ArgumentNullException.ThrowIfNull(envelope);

        published = null;
        stateEvent = null;
        state = null;
        if (!TryUnpack(envelope, out published) || published == null)
            return false;

        stateEvent = published.StateEvent;
        var stateDescriptor = new TState().Descriptor;
        if (published.StateRoot == null ||
            !published.StateRoot.Is(stateDescriptor))
        {
            return false;
        }

        state = published.StateRoot.Unpack<TState>();
        return true;
    }

    public static bool TryGetObservedPayload(
        EventEnvelope envelope,
        out Google.Protobuf.WellKnownTypes.Any? payload,
        out string eventId,
        out long stateVersion)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        payload = null;
        eventId = string.Empty;
        stateVersion = 0;

        if (!TryUnpack(envelope, out var published) || published?.StateEvent?.EventData == null)
            return false;

        payload = published.StateEvent.EventData;
        eventId = published.StateEvent.EventId ?? string.Empty;
        stateVersion = published.StateEvent.Version;
        return true;
    }

    public static bool TryCreateObservedEnvelope(
        EventEnvelope envelope,
        out EventEnvelope? observedEnvelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        observedEnvelope = null;
        if (!TryUnpack(envelope, out var published) ||
            published?.StateEvent?.EventData == null)
        {
            return false;
        }

        observedEnvelope = envelope.Clone();
        observedEnvelope.Payload = published.StateEvent.EventData.Clone();
        if (!string.IsNullOrWhiteSpace(published.StateEvent.EventId))
            observedEnvelope.Id = published.StateEvent.EventId;
        if (published.StateEvent.Timestamp != null)
            observedEnvelope.Timestamp = published.StateEvent.Timestamp.Clone();
        return true;
    }

    public static DateTimeOffset ResolveTimestamp(
        EventEnvelope envelope,
        DateTimeOffset fallbackUtcNow)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (TryUnpack(envelope, out var published))
        {
            if (published?.StateEvent?.Timestamp != null)
                return published.StateEvent.Timestamp.ToDateTimeOffset();

            return EventEnvelopeTimestampResolver.Resolve(envelope, fallbackUtcNow);
        }

        return fallbackUtcNow;
    }
}
