using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.StreamingProxy;

public sealed class StreamingProxyRoomSessionEventProjector
    : ProjectionSessionEventProjectorBase<StreamingProxyRoomSessionProjectionContext, StreamingProxyRoomSessionEnvelope>
{
    public StreamingProxyRoomSessionEventProjector(
        IProjectionSessionEventHub<StreamingProxyRoomSessionEnvelope> sessionEventHub)
        : base(sessionEventHub)
    {
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<StreamingProxyRoomSessionEnvelope>> ResolveSessionEventEntries(
        StreamingProxyRoomSessionProjectionContext context,
        EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.IsNullOrWhiteSpace(context.RootActorId) || string.IsNullOrWhiteSpace(context.SessionId))
            return EmptyEntries;

        if (!ShouldPublish(context, envelope))
            return EmptyEntries;

        return
        [
            new ProjectionSessionEventEntry<StreamingProxyRoomSessionEnvelope>(
                context.RootActorId,
                context.SessionId,
                new StreamingProxyRoomSessionEnvelope
                {
                    Envelope = envelope,
                }),
        ];
    }

    private static bool ShouldPublish(
        StreamingProxyRoomSessionProjectionContext context,
        EventEnvelope envelope)
    {
        if (string.Equals(
                context.ProjectionKind,
                StreamingProxyProjectionKinds.RoomSubscriptionSession,
                StringComparison.Ordinal))
        {
            return true;
        }

        if (!TryGetObservedPayload(envelope, out var payload))
            return false;

        if (payload.Is(GroupChatTopicEvent.Descriptor))
            return string.Equals(payload.Unpack<GroupChatTopicEvent>().SessionId, context.SessionId, StringComparison.Ordinal);

        if (payload.Is(GroupChatMessageEvent.Descriptor))
            return string.Equals(payload.Unpack<GroupChatMessageEvent>().SessionId, context.SessionId, StringComparison.Ordinal);

        if (payload.Is(StreamingProxyChatSessionTerminalStateChanged.Descriptor))
        {
            return string.Equals(
                payload.Unpack<StreamingProxyChatSessionTerminalStateChanged>().SessionId,
                context.SessionId,
                StringComparison.Ordinal);
        }

        if (payload.Is(GroupChatParticipantJoinedEvent.Descriptor) ||
            payload.Is(GroupChatParticipantLeftEvent.Descriptor))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetObservedPayload(EventEnvelope envelope, out Any payload)
    {
        payload = envelope.Payload!;
        if (CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var observedPayload, out _, out _) &&
            observedPayload != null)
        {
            payload = observedPayload;
        }

        return payload != null;
    }
}
