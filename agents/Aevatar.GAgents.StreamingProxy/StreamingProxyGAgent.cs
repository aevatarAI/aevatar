using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.StreamingProxy;

/// <summary>
/// Group chat room GAgent. Acts as a message broker for multiple external
/// OpenClaw agents. Does NOT call LLM itself — it receives messages from
/// participants and broadcasts them to all SSE subscribers.
/// </summary>
public sealed class StreamingProxyGAgent : GAgentBase<StreamingProxyGAgentState>
{
    [EventHandler(EndpointName = "initializeRoom")]
    public async Task HandleGroupChatRoomInitialized(GroupChatRoomInitializedEvent evt)
    {
        await PersistDomainEventAsync(evt);

        Logger.LogInformation("[StreamingProxy] Room initialized: {RoomName}", evt.RoomName);
    }

    /// <summary>
    /// Overrides base ChatRequestEvent handler. Instead of calling LLM,
    /// converts the user prompt into a group chat topic and broadcasts it.
    /// </summary>
    [EventHandler]
    public async Task HandleChatRequest(ChatRequestEvent request)
    {
        var topicEvent = new GroupChatTopicEvent
        {
            Prompt = request.Prompt,
            SessionId = request.SessionId,
        };

        await PersistDomainEventAsync(topicEvent);

        // Publish topic so all SSE subscribers (user + OpenClaws) receive it
        await PublishAsync(topicEvent, TopologyAudience.Parent);

        Logger.LogInformation(
            "[StreamingProxy] Topic started: {Preview}",
            request.Prompt.Length > 100 ? request.Prompt[..100] + "..." : request.Prompt);
    }

    [EventHandler(EndpointName = "postMessage")]
    public async Task HandleGroupChatMessage(GroupChatMessageEvent evt)
    {
        await PersistDomainEventAsync(evt);

        // Broadcast to all SSE subscribers
        await PublishAsync(evt, TopologyAudience.Parent);

        Logger.LogInformation(
            "[StreamingProxy] Message from {AgentName}: {Preview}",
            evt.AgentName,
            evt.Content.Length > 100 ? evt.Content[..100] + "..." : evt.Content);
    }

    [EventHandler(EndpointName = "joinRoom")]
    public async Task HandleGroupChatParticipantJoined(GroupChatParticipantJoinedEvent evt)
    {
        await PersistDomainEventAsync(evt);

        // Broadcast join notification
        await PublishAsync(evt, TopologyAudience.Parent);

        Logger.LogInformation("[StreamingProxy] Participant joined: {Name} ({Id})", evt.DisplayName, evt.AgentId);
    }

    [EventHandler(EndpointName = "leaveRoom")]
    public async Task HandleGroupChatParticipantLeft(GroupChatParticipantLeftEvent evt)
    {
        await PersistDomainEventAsync(evt);

        // Broadcast leave notification
        await PublishAsync(evt, TopologyAudience.Parent);

        Logger.LogInformation("[StreamingProxy] Participant left: {Id}", evt.AgentId);
    }

    /// <summary>
    /// Applies domain events to the sole authoritative actor state.
    /// Called by the event sourcing infrastructure after PersistDomainEventAsync.
    /// </summary>
    protected override StreamingProxyGAgentState TransitionState(StreamingProxyGAgentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<GroupChatRoomInitializedEvent>(ApplyRoomInitialized)
            .On<GroupChatTopicEvent>(ApplyTopic)
            .On<GroupChatMessageEvent>(ApplyMessage)
            .On<GroupChatParticipantJoinedEvent>(ApplyParticipantJoined)
            .On<GroupChatParticipantLeftEvent>(ApplyParticipantLeft)
            .OrCurrent();

    private static StreamingProxyGAgentState ApplyRoomInitialized(
        StreamingProxyGAgentState current,
        GroupChatRoomInitializedEvent evt)
    {
        var next = current.Clone();
        next.RoomName = evt.RoomName;
        return next;
    }

    private static StreamingProxyGAgentState ApplyTopic(
        StreamingProxyGAgentState current,
        GroupChatTopicEvent evt)
    {
        var next = current.Clone();
        next.NextSequence++;
        next.Messages.Add(new StreamingProxyChatMessage
        {
            Sequence = next.NextSequence,
            SenderAgentId = "user",
            SenderName = "User",
            Content = evt.Prompt,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            IsTopic = true,
        });
        TrimMessages(next);
        return next;
    }

    private static StreamingProxyGAgentState ApplyMessage(
        StreamingProxyGAgentState current,
        GroupChatMessageEvent evt)
    {
        var next = current.Clone();
        next.NextSequence++;
        next.Messages.Add(new StreamingProxyChatMessage
        {
            Sequence = next.NextSequence,
            SenderAgentId = evt.AgentId,
            SenderName = evt.AgentName,
            Content = evt.Content,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            IsTopic = false,
        });
        TrimMessages(next);
        return next;
    }

    private static StreamingProxyGAgentState ApplyParticipantJoined(
        StreamingProxyGAgentState current,
        GroupChatParticipantJoinedEvent evt)
    {
        var next = current.Clone();
        RemoveParticipant(next, evt.AgentId);
        next.Participants.Add(new StreamingProxyParticipant
        {
            AgentId = evt.AgentId,
            DisplayName = evt.DisplayName,
            JoinedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        return next;
    }

    private static StreamingProxyGAgentState ApplyParticipantLeft(
        StreamingProxyGAgentState current,
        GroupChatParticipantLeftEvent evt)
    {
        var next = current.Clone();
        RemoveParticipant(next, evt.AgentId);
        return next;
    }

    private static void RemoveParticipant(StreamingProxyGAgentState state, string agentId)
    {
        for (var i = state.Participants.Count - 1; i >= 0; i--)
        {
            if (string.Equals(state.Participants[i].AgentId, agentId, StringComparison.Ordinal))
                state.Participants.RemoveAt(i);
        }
    }

    private static void TrimMessages(StreamingProxyGAgentState state)
    {
        while (state.Messages.Count > StreamingProxyDefaults.MaxMessages)
        {
            state.Messages.RemoveAt(0);
        }
    }
}
