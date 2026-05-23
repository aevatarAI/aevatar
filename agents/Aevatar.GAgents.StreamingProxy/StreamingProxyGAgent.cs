using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.StreamingProxy;

/// <summary>
/// Refactor (iter43/issue-865-streaming-proxy-room-chat-host-orchestration):
///   Old pattern: StreamingProxy chat endpoint and participant coordinator fetch runtime actor objects, run Nyx participant discussion loops, mutate participant side-store state, and dispatch room events from Host/Application-side orchestration.
///   New principle: StreamingProxyGAgent owns participant admission, reply rounds, leave/failure decisions, and terminal-state publication; Host submits one typed command and observes projection/readmodel events only. Coordinator is adapter-only for Nyx external calls.
/// </summary>
public sealed class StreamingProxyGAgent : GAgentBase<StreamingProxyGAgentState>, IProjectedActor
{
    public static string ProjectionKind => StreamingProxyProjectionKinds.CurrentState;

    [EventHandler(EndpointName = "initializeRoom")]
    public async Task HandleGroupChatRoomInitialized(GroupChatRoomInitializedEvent evt)
    {
        await PersistDomainEventAsync(evt);

        Logger.LogInformation("[StreamingProxy] Room initialized: {RoomName}", evt.RoomName);
    }

    [EventHandler]
    public async Task HandleChatRequest(ChatRequestEvent request)
    {
        await PublishTopicAndTerminalAsync(
            request.Prompt,
            request.SessionId,
            StreamingProxyChatSessionTerminalStatus.Completed,
            null);
    }

    [EventHandler]
    public async Task HandleRoomChatRequested(StreamingProxyRoomChatRequested request)
    {
        var prompt = request.Prompt?.Trim() ?? string.Empty;
        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? Guid.NewGuid().ToString("N")
            : request.SessionId.Trim();

        if (string.IsNullOrWhiteSpace(prompt))
        {
            await PublishTerminalStateAsync(
                sessionId,
                StreamingProxyChatSessionTerminalStatus.Failed,
                "StreamingProxy chat prompt is required.");
            return;
        }

        var topic = await PublishTopicAsync(prompt, sessionId);
        var credentialHandles = Services.GetService<IStreamingProxyRoomCredentialHandleStore>();
        var accessToken = credentialHandles?.Consume(
            request.CredentialHandleId,
            new StreamingProxyRoomCredentialHandleScope(Id, request.ScopeId, sessionId));
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            await PublishTerminalStateAsync(
                sessionId,
                StreamingProxyChatSessionTerminalStatus.Completed,
                null);
            return;
        }

        var coordinator = Services.GetService<StreamingProxyNyxParticipantCoordinator>();
        if (coordinator == null)
        {
            await PublishTerminalStateAsync(
                sessionId,
                StreamingProxyChatSessionTerminalStatus.Completed,
                null);
            return;
        }

        var participants = await coordinator.ResolveParticipantsAsync(
            accessToken,
            NormalizeOptional(request.PreferredRoute),
            NormalizeOptional(request.DefaultModel),
            CancellationToken.None);
        var participant = participants.FirstOrDefault();
        if (participant == null)
        {
            await PublishTerminalStateAsync(
                sessionId,
                StreamingProxyChatSessionTerminalStatus.Completed,
                null);
            return;
        }

        await EnsureParticipantJoinedAsync(participant);
        var reply = await coordinator.GenerateReplyAsync(
            participant,
            participants,
            topic.Prompt,
            sessionId,
            accessToken,
            CancellationToken.None);
        if (reply.Status == StreamingProxyNyxParticipantReplyStatus.Succeeded &&
            !string.IsNullOrWhiteSpace(reply.Content))
        {
            await PublishParticipantMessageAsync(participant, reply.Content, sessionId);
            await PublishTerminalStateAsync(
                sessionId,
                StreamingProxyChatSessionTerminalStatus.Completed,
                null);
            return;
        }

        await PublishParticipantLeftAsync(participant.ParticipantId);
        await PublishTerminalStateAsync(
            sessionId,
            StreamingProxyChatSessionTerminalStatus.Failed,
            reply.ErrorMessage ?? "StreamingProxy participant reply failed.");
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

    [EventHandler(EndpointName = "completeSession")]
    public async Task HandleChatSessionTerminalStateChanged(StreamingProxyChatSessionTerminalStateChanged evt)
    {
        await PersistDomainEventAsync(evt);

        Logger.LogInformation(
            "[StreamingProxy] Session terminal state changed: room={RoomId} session={SessionId} status={Status}",
            Id,
            evt.SessionId,
            evt.Status);
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
            .On<StreamingProxyChatSessionTerminalStateChanged>(ApplyTerminalStateChanged)
            .OrCurrent();

    private async Task PublishTopicAndTerminalAsync(
        string prompt,
        string sessionId,
        StreamingProxyChatSessionTerminalStatus status,
        string? errorMessage)
    {
        await PublishTopicAsync(prompt, sessionId);
        await PublishTerminalStateAsync(sessionId, status, errorMessage);
    }

    private async Task<GroupChatTopicEvent> PublishTopicAsync(string prompt, string sessionId)
    {
        var topicEvent = new GroupChatTopicEvent
        {
            Prompt = prompt,
            SessionId = sessionId,
        };

        await PersistDomainEventAsync(topicEvent);
        await PublishAsync(topicEvent, TopologyAudience.Parent);

        Logger.LogInformation(
            "[StreamingProxy] Topic started: {Preview}",
            prompt.Length > 100 ? prompt[..100] + "..." : prompt);

        return topicEvent;
    }

    private async Task EnsureParticipantJoinedAsync(StreamingProxyNyxParticipantDefinition participant)
    {
        if (State.Participants.Any(existing =>
                string.Equals(existing.AgentId, participant.ParticipantId, StringComparison.Ordinal)))
        {
            return;
        }

        var evt = new GroupChatParticipantJoinedEvent
        {
            AgentId = participant.ParticipantId,
            DisplayName = participant.DisplayName,
        };

        await PersistDomainEventAsync(evt);
        await PublishAsync(evt, TopologyAudience.Parent);
    }

    private async Task PublishParticipantMessageAsync(
        StreamingProxyNyxParticipantDefinition participant,
        string content,
        string sessionId)
    {
        var evt = new GroupChatMessageEvent
        {
            AgentId = participant.ParticipantId,
            AgentName = participant.DisplayName,
            Content = content,
            SessionId = sessionId,
        };

        await PersistDomainEventAsync(evt);
        await PublishAsync(evt, TopologyAudience.Parent);
    }

    private async Task PublishParticipantLeftAsync(string participantId)
    {
        if (string.IsNullOrWhiteSpace(participantId) ||
            !State.Participants.Any(existing =>
                string.Equals(existing.AgentId, participantId, StringComparison.Ordinal)))
        {
            return;
        }

        var evt = new GroupChatParticipantLeftEvent
        {
            AgentId = participantId,
        };

        await PersistDomainEventAsync(evt);
        await PublishAsync(evt, TopologyAudience.Parent);
    }

    private async Task PublishTerminalStateAsync(
        string sessionId,
        StreamingProxyChatSessionTerminalStatus status,
        string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            State.TerminalSessions.ContainsKey(sessionId))
        {
            return;
        }

        var evt = new StreamingProxyChatSessionTerminalStateChanged
        {
            SessionId = sessionId,
            Status = status,
            TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ErrorMessage = errorMessage ?? string.Empty,
        };

        await PersistDomainEventAsync(evt);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

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

    private static StreamingProxyGAgentState ApplyTerminalStateChanged(
        StreamingProxyGAgentState current,
        StreamingProxyChatSessionTerminalStateChanged evt)
    {
        var next = current.Clone();
        if (string.IsNullOrWhiteSpace(evt.SessionId))
            return next;

        next.TerminalSessions[evt.SessionId] = new StreamingProxyChatSessionTerminalRecord
        {
            SessionId = evt.SessionId,
            Status = evt.Status,
            TerminalAt = evt.TerminalAt,
            ErrorMessage = evt.ErrorMessage ?? string.Empty,
        };
        return next;
    }
}
