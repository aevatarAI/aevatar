using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
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
// Refactor (iter56/cluster-894-nyx-coordinator-adapter-only): old=coordinator-owned facts, new=adapter-only + room-actor-owned facts
// Room effects from adapters enter as typed request payloads through the actor inbox.
// This actor remains the only component that converts those requests into committed room domain events.
// External Nyx streaming I/O stays outside actor turns.
[GAgent(StreamingProxyDefaults.GAgentKind)]
public sealed class StreamingProxyGAgent : GAgentBase<StreamingProxyGAgentState>, IProjectedActor
{
    internal const string ChatLifecycleContinuationRunnerStreamId = "streaming-proxy:chat-lifecycle-continuation-runner";

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
        var sessionId = request.SessionId?.Trim() ?? string.Empty;
        var scopeId = request.ScopeId?.Trim() ?? string.Empty;
        var prompt = request.Prompt?.Trim() ?? string.Empty;
        var lifecycleEvent = new StreamingProxyChatLifecycleAcceptedEvent
        {
            SessionId = sessionId,
            ScopeId = scopeId,
        };
        var toolContext = AgentToolExecutionContextMapper.FromPayload(request.ToolContext);
        if (!string.IsNullOrWhiteSpace(toolContext.Credentials.AccessToken))
            lifecycleEvent.AccessToken = toolContext.Credentials.AccessToken;
        if (!string.IsNullOrWhiteSpace(toolContext.Routing.NyxIdRoutePreference))
            lifecycleEvent.PreferredRoute = toolContext.Routing.NyxIdRoutePreference;
        if (!string.IsNullOrWhiteSpace(toolContext.Routing.ModelOverride))
            lifecycleEvent.DefaultModel = toolContext.Routing.ModelOverride;
        lifecycleEvent.Prompt = prompt;

        var topicEvent = new GroupChatTopicEvent
        {
            Prompt = prompt,
            SessionId = sessionId,
        };

        await PersistDomainEventAsync(lifecycleEvent);
        await PersistDomainEventAsync(topicEvent);

        // Publish topic so all SSE subscribers (user + OpenClaws) receive it
        await PublishAsync(topicEvent, TopologyAudience.Parent);

        if (!string.IsNullOrWhiteSpace(lifecycleEvent.AccessToken))
        {
            await SendToAsync(
                ChatLifecycleContinuationRunnerStreamId,
                new StreamingProxyChatLifecycleContinuationRequested
                {
                    RoomId = Id,
                    SessionId = sessionId,
                    ScopeId = scopeId,
                    Prompt = prompt,
                    AccessToken = lifecycleEvent.AccessToken,
                    PreferredRoute = lifecycleEvent.PreferredRoute,
                    DefaultModel = lifecycleEvent.DefaultModel,
                });
        }

        Logger.LogInformation(
            "[StreamingProxy] Topic started: {Preview}",
            prompt.Length > 100 ? prompt[..100] + "..." : prompt);
    }

    [EventHandler(EndpointName = "continueChatLifecycle")]
    public async Task HandleChatLifecycleContinuationRequested(StreamingProxyChatLifecycleContinuationRequested request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.AccessToken))
            return;

        if (string.IsNullOrWhiteSpace(request.RoomId))
            request.RoomId = Id;

        await SendToAsync(ChatLifecycleContinuationRunnerStreamId, request);
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

    [EventHandler(EndpointName = "requestPostMessage")]
    public async Task HandleParticipantMessageRequested(StreamingProxyParticipantMessageRequested request)
    {
        // Refactor (iter56/cluster-894-nyx-coordinator-adapter-only): old=coordinator-owned facts, new=adapter-only + room-actor-owned facts
        // Room command adapters now submit request payloads instead of committed room facts.
        // This actor validates its authoritative state boundary and mints the committed message event.
        // Projection and SSE continue to observe only the existing committed event types.
        await HandleGroupChatMessage(new GroupChatMessageEvent
        {
            AgentId = request.AgentId,
            AgentName = string.IsNullOrWhiteSpace(request.AgentName) ? request.AgentId : request.AgentName,
            Content = request.Content,
            SessionId = request.SessionId,
        });
    }

    [EventHandler(EndpointName = "resolveChatParticipants")]
    public async Task HandleChatParticipantsResolvedRequested(StreamingProxyChatParticipantsResolvedRequested request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!State.ChatLifecycles.TryGetValue(request.SessionId, out var lifecycle))
            return;

        var participants = NormalizeLifecycleParticipants(request.Participants);
        var maxRounds = participants.Count > 1 ? StreamingProxyDefaults.MaxDiscussionRounds : participants.Count == 1 ? 1 : 0;
        await PersistDomainEventAsync(new StreamingProxyChatLifecycleParticipantsResolvedEvent
        {
            SessionId = request.SessionId,
            MaxRounds = maxRounds,
            Participants = { participants },
        });

        if (participants.Count == 0)
        {
            await CommitParticipantTerminalAsync(request.SessionId, 0);
            return;
        }

        await SendNextParticipantReplyRequestAsync(State.ChatLifecycles[request.SessionId]);
    }

    [EventHandler(EndpointName = "observeParticipantReply")]
    public async Task HandleParticipantReplyObservedRequested(StreamingProxyChatParticipantReplyObservedRequested request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!State.ChatLifecycles.TryGetValue(request.SessionId, out var lifecycle) ||
            !IsCurrentParticipant(lifecycle, request.ParticipantId, request.Round, request.ParticipantIndex, out var participant) ||
            string.IsNullOrWhiteSpace(request.Content))
        {
            return;
        }

        var messageSequence = State.NextSequence + 1;
        await PersistDomainEventAsync(new StreamingProxyChatParticipantReplyRecordedEvent
        {
            SessionId = request.SessionId,
            ParticipantId = request.ParticipantId,
            Round = request.Round,
            ParticipantIndex = request.ParticipantIndex,
            Content = request.Content.Trim(),
            MessageSequence = messageSequence,
        });

        var messageEvent = new GroupChatMessageEvent
        {
            AgentId = participant.ParticipantId,
            AgentName = string.IsNullOrWhiteSpace(participant.DisplayName) ? participant.ParticipantId : participant.DisplayName,
            Content = request.Content.Trim(),
            SessionId = request.SessionId,
        };
        await PersistDomainEventAsync(messageEvent);

        await PublishAsync(messageEvent, TopologyAudience.Parent);
        await ContinueOrCompleteLifecycleAsync(request.SessionId);
    }

    [EventHandler(EndpointName = "observeParticipantReplyFailed")]
    public async Task HandleParticipantReplyFailedRequested(StreamingProxyChatParticipantReplyFailedRequested request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!State.ChatLifecycles.TryGetValue(request.SessionId, out var lifecycle) ||
            !IsCurrentParticipant(lifecycle, request.ParticipantId, request.Round, request.ParticipantIndex, out _))
        {
            return;
        }

        await PersistDomainEventAsync(new StreamingProxyChatParticipantReplyFailedEvent
        {
            SessionId = request.SessionId,
            ParticipantId = request.ParticipantId,
            Round = request.Round,
            ParticipantIndex = request.ParticipantIndex,
            FailureKind = request.FailureKind,
            ErrorMessage = request.ErrorMessage ?? string.Empty,
        });

        if (HasParticipant(request.ParticipantId))
        {
            await HandleGroupChatParticipantLeft(new GroupChatParticipantLeftEvent
            {
                AgentId = request.ParticipantId,
            });
        }

        await ContinueOrCompleteLifecycleAsync(request.SessionId);
    }

    [EventHandler(EndpointName = "joinRoom")]
    public async Task HandleGroupChatParticipantJoined(GroupChatParticipantJoinedEvent evt)
    {
        if (HasParticipant(evt.AgentId))
        {
            Logger.LogInformation("[StreamingProxy] Participant already joined: {Name} ({Id})", evt.DisplayName, evt.AgentId);
            return;
        }

        await PersistDomainEventAsync(evt);

        // Broadcast join notification
        await PublishAsync(evt, TopologyAudience.Parent);

        Logger.LogInformation("[StreamingProxy] Participant joined: {Name} ({Id})", evt.DisplayName, evt.AgentId);
    }

    [EventHandler(EndpointName = "requestJoinRoom")]
    public async Task HandleParticipantJoinRequested(StreamingProxyParticipantJoinRequested request)
    {
        // Refactor (iter56/cluster-894-nyx-coordinator-adapter-only): old=coordinator-owned facts, new=adapter-only + room-actor-owned facts
        // Join requests are command input, not already-committed participant facts.
        // Idempotent participant ownership stays inside this room actor state.
        // Downstream projections still receive GroupChatParticipantJoinedEvent only after this handler commits it.
        await HandleGroupChatParticipantJoined(new GroupChatParticipantJoinedEvent
        {
            AgentId = request.AgentId,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.AgentId : request.DisplayName,
        });
    }

    [EventHandler(EndpointName = "leaveRoom")]
    public async Task HandleGroupChatParticipantLeft(GroupChatParticipantLeftEvent evt)
    {
        await PersistDomainEventAsync(evt);

        // Broadcast leave notification
        await PublishAsync(evt, TopologyAudience.Parent);

        Logger.LogInformation("[StreamingProxy] Participant left: {Id}", evt.AgentId);
    }

    [EventHandler(EndpointName = "requestLeaveRoom")]
    public async Task HandleParticipantLeaveRequested(StreamingProxyParticipantLeaveRequested request)
    {
        // Refactor (iter56/cluster-894-nyx-coordinator-adapter-only): old=coordinator-owned facts, new=adapter-only + room-actor-owned facts
        // Leave requests report adapter observations; this actor owns whether a leave fact is committed.
        // Missing participants remain a no-op so stale Nyx failures cannot invent room history.
        // Committed leave events remain the only projection/SSE participant removal signal.
        if (!HasParticipant(request.AgentId))
        {
            Logger.LogInformation("[StreamingProxy] Participant leave ignored because participant is not joined: {Id}", request.AgentId);
            return;
        }

        await HandleGroupChatParticipantLeft(new GroupChatParticipantLeftEvent
        {
            AgentId = request.AgentId,
        });
    }

    [EventHandler(EndpointName = "completeSession")]
    public async Task HandleChatSessionTerminalStateChanged(StreamingProxyChatSessionTerminalStateChanged evt)
    {
        // Refactor (iter47/issue-877-chat-endpoints-own-lifecycle-and-compensation):
        //   Old pattern: Chat endpoints owned actor lifecycle, registry compensation, participant orchestration, terminal-state recovery, and chat history command-port side effects.
        //   New principle: Endpoint is adapter-only (HTTP/SSE); typed command facade owns lifecycle; existing chat actors own compensation events and terminal-state publication.
        await PersistDomainEventAsync(evt);

        Logger.LogInformation(
            "[StreamingProxy] Session terminal state changed: room={RoomId} session={SessionId} status={Status}",
            Id,
            evt.SessionId,
            evt.Status);
    }

    [EventHandler(EndpointName = "requestCompleteSession")]
    public async Task HandleSessionTerminalStateRequested(StreamingProxySessionTerminalStateRequested request)
    {
        // Refactor (iter56/cluster-894-nyx-coordinator-adapter-only): old=coordinator-owned facts, new=adapter-only + room-actor-owned facts
        // Terminal requests carry observed adapter outcome; this actor owns the committed terminal fact.
        // The actor stamps terminal time at commit so callers cannot imply a stronger ACK than dispatch.
        // Existing terminal projection remains keyed by StreamingProxyChatSessionTerminalStateChanged.
        await HandleChatSessionTerminalStateChanged(new StreamingProxyChatSessionTerminalStateChanged
        {
            SessionId = request.SessionId,
            Status = request.Status,
            TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ErrorMessage = request.ErrorMessage ?? string.Empty,
        });
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
            .On<StreamingProxyChatLifecycleAcceptedEvent>(ApplyLifecycleAccepted)
            .On<StreamingProxyChatLifecycleParticipantsResolvedEvent>(ApplyLifecycleParticipantsResolved)
            .On<StreamingProxyChatParticipantReplyRecordedEvent>(ApplyParticipantReplyRecorded)
            .On<StreamingProxyChatParticipantReplyFailedEvent>(ApplyParticipantReplyFailed)
            .On<StreamingProxyChatSessionTerminalStateChanged>(ApplyTerminalStateChanged)
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
        // Refactor (iter50/issue-887-streaming-proxy-participant-authority):
        //   Old pattern: StreamingProxyGAgent and singleton StreamingProxyParticipantGAgent both held participant fact; reads went to singleton readmodel, writes to both — dual fact source.
        //   New principle: StreamingProxyGAgent per room is the single participant authority; singleton actor/store/readmodel deleted; reads go through room current-state projection.
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

    private bool HasParticipant(string agentId) =>
        State.Participants.Any(participant =>
            string.Equals(participant.AgentId, agentId, StringComparison.OrdinalIgnoreCase));

    private static void RemoveParticipant(StreamingProxyGAgentState state, string agentId)
    {
        for (var i = state.Participants.Count - 1; i >= 0; i--)
        {
            if (string.Equals(state.Participants[i].AgentId, agentId, StringComparison.OrdinalIgnoreCase))
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
        next.ChatLifecycles.Remove(evt.SessionId);
        return next;
    }

    private static StreamingProxyGAgentState ApplyLifecycleAccepted(
        StreamingProxyGAgentState current,
        StreamingProxyChatLifecycleAcceptedEvent evt)
    {
        var next = current.Clone();
        if (string.IsNullOrWhiteSpace(evt.SessionId))
            return next;

        next.ChatLifecycles[evt.SessionId] = new StreamingProxyChatLifecycleRecord
        {
            SessionId = evt.SessionId,
            ScopeId = evt.ScopeId,
            AccessToken = evt.AccessToken,
            PreferredRoute = evt.PreferredRoute,
            DefaultModel = evt.DefaultModel,
            Prompt = evt.Prompt,
        };
        return next;
    }

    private async Task ContinueOrCompleteLifecycleAsync(string sessionId)
    {
        if (!State.ChatLifecycles.TryGetValue(sessionId, out var lifecycle))
            return;

        if (TryGetCurrentActiveParticipant(lifecycle, out _))
        {
            await SendNextParticipantReplyRequestAsync(lifecycle);
            return;
        }

        await CommitParticipantTerminalAsync(sessionId, lifecycle.SuccessfulReplyCount);
    }

    private async Task CommitParticipantTerminalAsync(string sessionId, int successfulReplyCount)
    {
        var terminalStatus = successfulReplyCount > 0
            ? StreamingProxyChatSessionTerminalStatus.Completed
            : StreamingProxyChatSessionTerminalStatus.Failed;
        var errorMessage = successfulReplyCount > 0
            ? string.Empty
            : "StreamingProxy chat completed without any participant replies.";

        await HandleChatSessionTerminalStateChanged(new StreamingProxyChatSessionTerminalStateChanged
        {
            SessionId = sessionId,
            Status = terminalStatus,
            TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ErrorMessage = errorMessage,
        });
    }

    private Task SendNextParticipantReplyRequestAsync(StreamingProxyChatLifecycleRecord lifecycle)
    {
        if (!TryGetCurrentActiveParticipant(lifecycle, out var participant))
            return Task.CompletedTask;

        var transcript = State.Messages
            .Where(message => !message.IsTopic)
            .Select(message => new StreamingProxyChatTranscriptEntry
            {
                Speaker = message.SenderName,
                Content = message.Content,
            })
            .ToList();
        var activeParticipants = lifecycle.Participants
            .Where(candidate => candidate.Status == StreamingProxyChatLifecycleParticipantStatus.Active)
            .Select(candidate => candidate.Clone())
            .ToList();

        return SendToAsync(
            ChatLifecycleContinuationRunnerStreamId,
            new StreamingProxyChatParticipantReplyRequested
            {
                RoomId = Id,
                SessionId = lifecycle.SessionId,
                ParticipantId = participant.ParticipantId,
                DisplayName = participant.DisplayName,
                RoutePreference = participant.RoutePreference,
                Model = participant.Model,
                Round = lifecycle.CurrentRound,
                ParticipantIndex = lifecycle.NextParticipantIndex,
                Prompt = lifecycle.Prompt,
                AccessToken = lifecycle.AccessToken,
                PreferredRoute = lifecycle.PreferredRoute,
                DefaultModel = lifecycle.DefaultModel,
                MaxRounds = lifecycle.MaxRounds,
                ActiveParticipants = { activeParticipants },
                Transcript = { transcript },
            });
    }

    private static bool IsCurrentParticipant(
        StreamingProxyChatLifecycleRecord lifecycle,
        string participantId,
        int round,
        int participantIndex,
        out StreamingProxyChatLifecycleParticipant participant)
    {
        participant = new StreamingProxyChatLifecycleParticipant();
        if (round != lifecycle.CurrentRound ||
            participantIndex != lifecycle.NextParticipantIndex ||
            participantIndex < 0 ||
            participantIndex >= lifecycle.Participants.Count)
        {
            return false;
        }

        var candidate = lifecycle.Participants[participantIndex];
        if (candidate.Status != StreamingProxyChatLifecycleParticipantStatus.Active ||
            !string.Equals(candidate.ParticipantId, participantId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        participant = candidate;
        return true;
    }

    private static bool TryGetCurrentActiveParticipant(
        StreamingProxyChatLifecycleRecord lifecycle,
        out StreamingProxyChatLifecycleParticipant participant)
    {
        participant = new StreamingProxyChatLifecycleParticipant();
        if (lifecycle.MaxRounds == 0 ||
            lifecycle.CurrentRound > lifecycle.MaxRounds ||
            lifecycle.NextParticipantIndex < 0)
        {
            return false;
        }

        for (var i = lifecycle.NextParticipantIndex; i < lifecycle.Participants.Count; i++)
        {
            if (lifecycle.Participants[i].Status == StreamingProxyChatLifecycleParticipantStatus.Active)
            {
                participant = lifecycle.Participants[i];
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<StreamingProxyChatLifecycleParticipant> NormalizeLifecycleParticipants(
        IEnumerable<StreamingProxyChatLifecycleParticipant> participants)
    {
        var result = new List<StreamingProxyChatLifecycleParticipant>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var participant in participants)
        {
            var participantId = participant.ParticipantId?.Trim();
            if (string.IsNullOrWhiteSpace(participantId) ||
                !seen.Add(participantId))
            {
                continue;
            }

            result.Add(new StreamingProxyChatLifecycleParticipant
            {
                ParticipantId = participantId,
                DisplayName = string.IsNullOrWhiteSpace(participant.DisplayName)
                    ? participantId
                    : participant.DisplayName.Trim(),
                RoutePreference = participant.RoutePreference?.Trim() ?? string.Empty,
                Model = participant.Model?.Trim() ?? string.Empty,
                Status = StreamingProxyChatLifecycleParticipantStatus.Active,
            });
        }

        return result;
    }

    private static StreamingProxyGAgentState ApplyLifecycleParticipantsResolved(
        StreamingProxyGAgentState current,
        StreamingProxyChatLifecycleParticipantsResolvedEvent evt)
    {
        var next = current.Clone();
        if (!next.ChatLifecycles.TryGetValue(evt.SessionId, out var lifecycle))
            return next;

        lifecycle.Participants.Clear();
        lifecycle.Participants.Add(evt.Participants);
        lifecycle.MaxRounds = evt.MaxRounds;
        lifecycle.CurrentRound = evt.Participants.Count == 0 ? 0 : 1;
        lifecycle.NextParticipantIndex = 0;
        lifecycle.SuccessfulReplyCount = 0;
        return next;
    }

    private static StreamingProxyGAgentState ApplyParticipantReplyRecorded(
        StreamingProxyGAgentState current,
        StreamingProxyChatParticipantReplyRecordedEvent evt)
    {
        var next = current.Clone();
        if (!next.ChatLifecycles.TryGetValue(evt.SessionId, out var lifecycle))
            return next;

        lifecycle.SuccessfulReplyCount++;
        AdvanceLifecycleCursor(lifecycle);
        return next;
    }

    private static StreamingProxyGAgentState ApplyParticipantReplyFailed(
        StreamingProxyGAgentState current,
        StreamingProxyChatParticipantReplyFailedEvent evt)
    {
        var next = current.Clone();
        if (!next.ChatLifecycles.TryGetValue(evt.SessionId, out var lifecycle))
            return next;

        var participant = lifecycle.Participants.FirstOrDefault(candidate =>
            string.Equals(candidate.ParticipantId, evt.ParticipantId, StringComparison.OrdinalIgnoreCase));
        if (participant != null)
        {
            participant.Status = StreamingProxyChatLifecycleParticipantStatus.Failed;
            participant.FailedRound = evt.Round;
            participant.FailureReason = evt.ErrorMessage ?? string.Empty;
        }

        AdvanceLifecycleCursor(lifecycle);
        return next;
    }

    private static void AdvanceLifecycleCursor(StreamingProxyChatLifecycleRecord lifecycle)
    {
        if (lifecycle.Participants.Count == 0)
            return;

        lifecycle.NextParticipantIndex++;
        while (lifecycle.NextParticipantIndex < lifecycle.Participants.Count &&
               lifecycle.Participants[lifecycle.NextParticipantIndex].Status != StreamingProxyChatLifecycleParticipantStatus.Active)
        {
            lifecycle.NextParticipantIndex++;
        }

        if (lifecycle.NextParticipantIndex < lifecycle.Participants.Count)
            return;

        lifecycle.CurrentRound++;
        lifecycle.NextParticipantIndex = 0;
        while (lifecycle.NextParticipantIndex < lifecycle.Participants.Count &&
               lifecycle.Participants[lifecycle.NextParticipantIndex].Status != StreamingProxyChatLifecycleParticipantStatus.Active)
        {
            lifecycle.NextParticipantIndex++;
        }
    }
}
