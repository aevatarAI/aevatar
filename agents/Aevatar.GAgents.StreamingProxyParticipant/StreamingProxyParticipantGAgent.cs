using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.StreamingProxyParticipant;

/// <summary>
/// Singleton actor that tracks streaming proxy room participants.
/// Replaces the chrono-storage backed <c>ChronoStorageStreamingProxyParticipantStore</c>.
///
/// Actor ID: <c>streaming-proxy-participants</c> (cluster-scoped singleton).
/// </summary>
public sealed class StreamingProxyParticipantGAgent
    : GAgentBase<StreamingProxyParticipantGAgentState>
{
    [EventHandler(EndpointName = "addParticipant")]
    public async Task HandleParticipantAdded(ParticipantAddedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.RoomId) || string.IsNullOrWhiteSpace(evt.AgentId))
            return;

        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "removeRoomParticipants")]
    public async Task HandleRoomParticipantsRemoved(RoomParticipantsRemovedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.RoomId))
            return;

        // Idempotent: skip if room does not exist
        if (!State.Rooms.ContainsKey(evt.RoomId))
            return;

        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "removeParticipant")]
    public async Task HandleParticipantRemoved(ParticipantRemovedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.RoomId) || string.IsNullOrWhiteSpace(evt.AgentId))
            return;

        if (!State.Rooms.TryGetValue(evt.RoomId, out var list) ||
            !list.Participants.Any(p => string.Equals(p.AgentId, evt.AgentId, StringComparison.Ordinal)))
        {
            return;
        }

        await PersistDomainEventAsync(evt);
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
    }

    protected override StreamingProxyParticipantGAgentState TransitionState(
        StreamingProxyParticipantGAgentState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<ParticipantAddedEvent>(ApplyParticipantAdded)
            .On<ParticipantRemovedEvent>(ApplyParticipantRemoved)
            .On<RoomParticipantsRemovedEvent>(ApplyRoomRemoved)
            .OrCurrent();
    }

    private static StreamingProxyParticipantGAgentState ApplyParticipantAdded(
        StreamingProxyParticipantGAgentState state, ParticipantAddedEvent evt)
    {
        var next = state.Clone();

        if (!next.Rooms.TryGetValue(evt.RoomId, out var list))
        {
            list = new ParticipantList();
            next.Rooms[evt.RoomId] = list;
        }

        // Remove existing entry for the same agent (upsert semantics)
        var existing = list.Participants.FirstOrDefault(p =>
            string.Equals(p.AgentId, evt.AgentId, StringComparison.Ordinal));
        if (existing is not null)
            list.Participants.Remove(existing);

        list.Participants.Add(new ParticipantEntry
        {
            AgentId = evt.AgentId,
            DisplayName = evt.DisplayName,
            JoinedAt = evt.JoinedAt,
        });

        return next;
    }

    private static StreamingProxyParticipantGAgentState ApplyParticipantRemoved(
        StreamingProxyParticipantGAgentState state, ParticipantRemovedEvent evt)
    {
        var next = state.Clone();
        if (!next.Rooms.TryGetValue(evt.RoomId, out var list))
            return next;

        for (var index = list.Participants.Count - 1; index >= 0; index--)
        {
            if (string.Equals(list.Participants[index].AgentId, evt.AgentId, StringComparison.Ordinal))
                list.Participants.RemoveAt(index);
        }

        if (list.Participants.Count == 0)
            next.Rooms.Remove(evt.RoomId);

        return next;
    }

    private static StreamingProxyParticipantGAgentState ApplyRoomRemoved(
        StreamingProxyParticipantGAgentState state, RoomParticipantsRemovedEvent evt)
    {
        var next = state.Clone();
        next.Rooms.Remove(evt.RoomId);
        return next;
    }

}
