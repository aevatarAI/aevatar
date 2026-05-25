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
/// Group chat room GAgent. Acts as a message broker for multiple external
/// OpenClaw agents. Does NOT call LLM itself — it receives messages from
/// participants and broadcasts them to all SSE subscribers.
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

    [EventHandler]
    public async Task HandleNyxDiscussionRequested(StreamingProxyNyxDiscussionRequested evt)
    {
        // Fix (review round 1, F1):
        //   Reviewer found Nyx transcript, active set, rounds, pruning, and stop decisions in coordinator locals.
        //   This actor now persists the discussion session and issues one participant work item at a time.
        if (string.IsNullOrWhiteSpace(evt.SessionId))
            return;

        if (evt.Participants.Count == 0)
        {
            await PersistDomainEventAsync(BuildTerminalEvent(
                evt.SessionId,
                StreamingProxyChatSessionTerminalStatus.Failed,
                "StreamingProxy chat completed without any participant replies."));
            return;
        }

        await PersistDomainEventAsync(new StreamingProxyNyxDiscussionStarted
        {
            SessionId = evt.SessionId,
            Prompt = evt.Prompt,
            TotalRounds = evt.Participants.Count > 1 ? StreamingProxyDefaults.MaxDiscussionRounds : 1,
            Participants = { evt.Participants.Select(participant => participant.Clone()) },
        });

        await ContinueNyxDiscussionAsync(evt.SessionId, evt.AccessToken);
    }

    [EventHandler]
    public async Task HandleNyxParticipantReplySucceeded(StreamingProxyNyxParticipantReplySucceeded evt)
    {
        if (!IsExpectedNyxParticipantTurn(evt.SessionId, evt.Round, evt.ParticipantId))
            return;

        var content = evt.Content?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            await HandleNyxParticipantReplyFailed(new StreamingProxyNyxParticipantReplyFailed
            {
                SessionId = evt.SessionId,
                AccessToken = evt.AccessToken,
                Round = evt.Round,
                ParticipantId = evt.ParticipantId,
                DisplayName = evt.DisplayName,
                ErrorMessage = "Participant returned an empty response.",
            });
            return;
        }

        await PersistDomainEventAsync(new StreamingProxyNyxParticipantReplyRecorded
        {
            SessionId = evt.SessionId,
            Round = evt.Round,
            ParticipantId = evt.ParticipantId,
            DisplayName = evt.DisplayName,
            Content = content,
        });

        await HandleGroupChatMessage(new GroupChatMessageEvent
        {
            AgentId = evt.ParticipantId,
            AgentName = evt.DisplayName,
            Content = content,
            SessionId = evt.SessionId,
        });

        await ContinueNyxDiscussionAsync(evt.SessionId, evt.AccessToken);
    }

    [EventHandler]
    public async Task HandleNyxParticipantReplyFailed(StreamingProxyNyxParticipantReplyFailed evt)
    {
        if (!IsExpectedNyxParticipantTurn(evt.SessionId, evt.Round, evt.ParticipantId))
            return;

        await PersistDomainEventAsync(new StreamingProxyNyxParticipantFailureRecorded
        {
            SessionId = evt.SessionId,
            Round = evt.Round,
            ParticipantId = evt.ParticipantId,
            DisplayName = evt.DisplayName,
            ErrorMessage = evt.ErrorMessage ?? string.Empty,
        });

        await HandleGroupChatParticipantLeft(new GroupChatParticipantLeftEvent
        {
            AgentId = evt.ParticipantId,
        });

        await ContinueNyxDiscussionAsync(evt.SessionId, evt.AccessToken);
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
            .On<StreamingProxyNyxDiscussionStarted>(ApplyNyxDiscussionStarted)
            .On<StreamingProxyNyxParticipantReplyRecorded>(ApplyNyxParticipantReplyRecorded)
            .On<StreamingProxyNyxParticipantFailureRecorded>(ApplyNyxParticipantFailureRecorded)
            .On<StreamingProxyNyxDiscussionRoundAdvanced>(ApplyNyxDiscussionRoundAdvanced)
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

    private async Task ContinueNyxDiscussionAsync(string sessionId, string accessToken)
    {
        if (!State.NyxDiscussionSessions.TryGetValue(sessionId, out var session) ||
            session.Status != StreamingProxyNyxDiscussionStatus.NyxDiscussionStatusActive)
        {
            return;
        }

        if (session.ActiveParticipants.Count == 0)
        {
            await CompleteNyxDiscussionAsync(session, StreamingProxyChatSessionTerminalStatus.Failed);
            return;
        }

        if (session.CurrentParticipantIndex >= session.ActiveParticipants.Count)
        {
            if (session.CurrentRoundSuccessfulReplies == 0 ||
                session.ActiveParticipants.Count < 2 ||
                session.CurrentRound >= session.TotalRounds)
            {
                await CompleteNyxDiscussionAsync(
                    session,
                    session.TotalSuccessfulReplies > 0
                        ? StreamingProxyChatSessionTerminalStatus.Completed
                        : StreamingProxyChatSessionTerminalStatus.Failed);
                return;
            }

            await PersistDomainEventAsync(new StreamingProxyNyxDiscussionRoundAdvanced
            {
                SessionId = session.SessionId,
                Round = session.CurrentRound + 1,
            });
            await ContinueNyxDiscussionAsync(sessionId, accessToken);
            return;
        }

        var participant = session.ActiveParticipants[session.CurrentParticipantIndex];
        var coordinator = Services.GetRequiredService<StreamingProxyNyxParticipantCoordinator>();
        await coordinator.RequestParticipantReplyAsync(
            Id,
            new StreamingProxyNyxParticipantWorkItem(
                session.SessionId,
                accessToken,
                session.CurrentRound,
                session.TotalRounds,
                ToParticipantDefinition(participant),
                session.ActiveParticipants.Select(ToParticipantDefinition).ToList(),
                session.Transcript.Select(entry => (entry.Speaker, entry.Content)).ToList())
            {
                Prompt = session.Prompt,
            },
            CancellationToken.None);
    }

    private async Task CompleteNyxDiscussionAsync(
        StreamingProxyNyxDiscussionSession session,
        StreamingProxyChatSessionTerminalStatus status)
    {
        var errorMessage = status == StreamingProxyChatSessionTerminalStatus.Failed
            ? "StreamingProxy chat completed without any participant replies."
            : string.Empty;
        await PersistDomainEventAsync(BuildTerminalEvent(session.SessionId, status, errorMessage));
    }

    private bool IsExpectedNyxParticipantTurn(string sessionId, int round, string participantId)
    {
        if (!State.NyxDiscussionSessions.TryGetValue(sessionId, out var session) ||
            session.Status != StreamingProxyNyxDiscussionStatus.NyxDiscussionStatusActive ||
            session.CurrentRound != round ||
            session.CurrentParticipantIndex < 0 ||
            session.CurrentParticipantIndex >= session.ActiveParticipants.Count)
        {
            return false;
        }

        return string.Equals(
            session.ActiveParticipants[session.CurrentParticipantIndex].ParticipantId,
            participantId,
            StringComparison.OrdinalIgnoreCase);
    }

    private static StreamingProxyChatSessionTerminalStateChanged BuildTerminalEvent(
        string sessionId,
        StreamingProxyChatSessionTerminalStatus status,
        string? errorMessage) =>
        new()
        {
            SessionId = sessionId,
            Status = status,
            TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ErrorMessage = errorMessage ?? string.Empty,
        };

    private static StreamingProxyNyxParticipantDefinition ToParticipantDefinition(
        StreamingProxyNyxParticipant participant) =>
        new(
            participant.ParticipantId,
            participant.RoutePreference,
            participant.DisplayName,
            participant.Model);

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
        if (next.NyxDiscussionSessions.TryGetValue(evt.SessionId, out var discussion))
        {
            var nextDiscussion = discussion.Clone();
            nextDiscussion.Status = evt.Status == StreamingProxyChatSessionTerminalStatus.Failed
                ? StreamingProxyNyxDiscussionStatus.NyxDiscussionStatusFailed
                : StreamingProxyNyxDiscussionStatus.NyxDiscussionStatusCompleted;
            nextDiscussion.TerminalErrorMessage = evt.ErrorMessage ?? string.Empty;
            next.NyxDiscussionSessions[evt.SessionId] = nextDiscussion;
        }

        return next;
    }

    private static StreamingProxyGAgentState ApplyNyxDiscussionStarted(
        StreamingProxyGAgentState current,
        StreamingProxyNyxDiscussionStarted evt)
    {
        var next = current.Clone();
        next.NyxDiscussionSessions[evt.SessionId] = new StreamingProxyNyxDiscussionSession
        {
            SessionId = evt.SessionId,
            Prompt = evt.Prompt,
            CurrentRound = 1,
            TotalRounds = Math.Max(evt.TotalRounds, 1),
            CurrentParticipantIndex = 0,
            CurrentRoundSuccessfulReplies = 0,
            TotalSuccessfulReplies = 0,
            Status = StreamingProxyNyxDiscussionStatus.NyxDiscussionStatusActive,
            ActiveParticipants = { evt.Participants.Select(participant => participant.Clone()) },
        };
        return next;
    }

    private static StreamingProxyGAgentState ApplyNyxParticipantReplyRecorded(
        StreamingProxyGAgentState current,
        StreamingProxyNyxParticipantReplyRecorded evt)
    {
        var next = current.Clone();
        if (!next.NyxDiscussionSessions.TryGetValue(evt.SessionId, out var session))
            return next;

        var nextSession = session.Clone();
        nextSession.Transcript.Add(new StreamingProxyNyxTranscriptEntry
        {
            Speaker = evt.DisplayName,
            Content = evt.Content,
        });
        TrimTranscript(nextSession);
        nextSession.CurrentParticipantIndex++;
        nextSession.CurrentRoundSuccessfulReplies++;
        nextSession.TotalSuccessfulReplies++;
        next.NyxDiscussionSessions[evt.SessionId] = nextSession;
        return next;
    }

    private static StreamingProxyGAgentState ApplyNyxParticipantFailureRecorded(
        StreamingProxyGAgentState current,
        StreamingProxyNyxParticipantFailureRecorded evt)
    {
        var next = current.Clone();
        if (!next.NyxDiscussionSessions.TryGetValue(evt.SessionId, out var session))
            return next;

        var nextSession = session.Clone();
        for (var i = nextSession.ActiveParticipants.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(
                    nextSession.ActiveParticipants[i].ParticipantId,
                    evt.ParticipantId,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            nextSession.ActiveParticipants.RemoveAt(i);
            if (i < nextSession.CurrentParticipantIndex)
                nextSession.CurrentParticipantIndex--;
        }

        if (nextSession.CurrentParticipantIndex > nextSession.ActiveParticipants.Count)
            nextSession.CurrentParticipantIndex = nextSession.ActiveParticipants.Count;

        next.NyxDiscussionSessions[evt.SessionId] = nextSession;
        return next;
    }

    private static StreamingProxyGAgentState ApplyNyxDiscussionRoundAdvanced(
        StreamingProxyGAgentState current,
        StreamingProxyNyxDiscussionRoundAdvanced evt)
    {
        var next = current.Clone();
        if (!next.NyxDiscussionSessions.TryGetValue(evt.SessionId, out var session))
            return next;

        var nextSession = session.Clone();
        nextSession.CurrentRound = evt.Round;
        nextSession.CurrentParticipantIndex = 0;
        nextSession.CurrentRoundSuccessfulReplies = 0;
        next.NyxDiscussionSessions[evt.SessionId] = nextSession;
        return next;
    }

    private static void TrimTranscript(StreamingProxyNyxDiscussionSession session)
    {
        while (session.Transcript.Count > StreamingProxyDefaults.MaxMessages)
        {
            session.Transcript.RemoveAt(0);
        }
    }
}
