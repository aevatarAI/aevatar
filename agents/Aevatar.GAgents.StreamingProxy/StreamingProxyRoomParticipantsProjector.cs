using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.StreamingProxy;

// Refactor (iter43/issue-865-streaming-proxy-room-chat-host-orchestration):
//   Old pattern: StreamingProxy chat endpoint and participant coordinator fetch runtime actor objects, run Nyx participant discussion loops, mutate participant side-store state, and dispatch room events from Host/Application-side orchestration.
//   New principle: StreamingProxyGAgent owns participant admission, reply rounds, leave/failure decisions, and terminal-state publication; Host submits one typed command and observes projection/readmodel events only. Coordinator is adapter-only for Nyx external calls.
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

    // Refactor (iter43/issue-865-streaming-proxy-room-chat-host-orchestration):
    //   Old pattern: StreamingProxy chat endpoint and participant coordinator fetch runtime actor objects, run Nyx participant discussion loops, mutate participant side-store state, and dispatch room events from Host/Application-side orchestration.
    //   New principle: StreamingProxyGAgent owns participant admission, reply rounds, leave/failure decisions, and terminal-state publication; Host submits one typed command and observes projection/readmodel events only. Coordinator is adapter-only for Nyx external calls.
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
            stateEvent == null ||
            state == null ||
            stateEvent.EventData == null ||
            !IsParticipantAffectingEvent(stateEvent.EventData))
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var snapshot = new StreamingProxyRoomParticipantsSnapshot
        {
            Id = ComposeSnapshotId(context.RootActorId),
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            RootActorId = context.RootActorId,
        };
        snapshot.Participants.Add(state.Participants);

        await _writeDispatcher.UpsertAsync(snapshot, ct);
    }

    public static string ComposeSnapshotId(string rootActorId) => rootActorId.Trim();

    private static bool IsParticipantAffectingEvent(Any payload) =>
        payload.Is(GroupChatParticipantJoinedEvent.Descriptor) ||
        payload.Is(GroupChatParticipantLeftEvent.Descriptor) ||
        payload.Is(GroupChatRoomInitializedEvent.Descriptor);
}
