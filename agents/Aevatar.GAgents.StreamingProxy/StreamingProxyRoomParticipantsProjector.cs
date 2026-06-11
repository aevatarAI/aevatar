using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.StreamingProxy;

public sealed class StreamingProxyRoomParticipantsProjector
    : ICurrentStateProjectionMaterializer<StreamingProxyCurrentStateProjectionContext>
{
    private readonly IProjectionWriteDispatcher<StreamingProxyRoomParticipantsSnapshot> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public StreamingProxyRoomParticipantsProjector(
        IProjectionWriteDispatcher<StreamingProxyRoomParticipantsSnapshot> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        StreamingProxyCurrentStateProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<StreamingProxyGAgentState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state == null)
        {
            return;
        }

        if (!stateEvent.EventData.Is(GroupChatParticipantJoinedEvent.Descriptor) &&
            !stateEvent.EventData.Is(GroupChatParticipantLeftEvent.Descriptor))
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var snapshot = new StreamingProxyRoomParticipantsSnapshot
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            RootActorId = context.RootActorId,
        };

        snapshot.Participants.AddRange(state.Participants.Select(participant =>
            new StreamingProxyRoomParticipantSnapshotEntry
            {
                AgentId = participant.AgentId,
                DisplayName = participant.DisplayName,
                JoinedAt = participant.JoinedAt,
            }));

        await _writeDispatcher.UpsertAsync(snapshot, ct);
    }
}
