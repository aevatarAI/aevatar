using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
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
// Refactor (iter56/cluster-894-nyx-coordinator-adapter-only): old=coordinator-owned facts, new=adapter-only + room-actor-owned facts
// Room effects from adapters enter as typed request payloads through the actor inbox.
// This actor remains the only component that converts those requests into committed room domain events.
// External Nyx streaming I/O stays outside actor turns.
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
        // Refactor (iter104/cluster-1): Old pattern: StreamingProxyChatLifecycleFacade owned chat continuation orchestration in Application layer. New principle: StreamingProxyGAgent typed continuation owns lifecycle; deprecated compat endpoints only normalize+dispatch typed command.
        var lifecycleEvent = new StreamingProxyChatLifecycleAcceptedEvent
        {
            SessionId = request.SessionId,
            ScopeId = request.ScopeId,
        };
        var toolContext = AgentToolExecutionContextMapper.FromPayload(request.ToolContext);
        if (!string.IsNullOrWhiteSpace(toolContext.Credentials.NyxIdAccessToken))
            lifecycleEvent.AccessToken = toolContext.Credentials.NyxIdAccessToken;
        if (!string.IsNullOrWhiteSpace(toolContext.Routing.NyxIdRoutePreference))
            lifecycleEvent.PreferredRoute = toolContext.Routing.NyxIdRoutePreference;
        if (!string.IsNullOrWhiteSpace(toolContext.Routing.ModelOverride))
            lifecycleEvent.DefaultModel = toolContext.Routing.ModelOverride;

        var topicEvent = new GroupChatTopicEvent
        {
            Prompt = request.Prompt,
            SessionId = request.SessionId,
        };

        await PersistDomainEventAsync(lifecycleEvent);
        await PersistDomainEventAsync(topicEvent);

        // Publish topic so all SSE subscribers (user + OpenClaws) receive it
        await PublishAsync(topicEvent, TopologyAudience.Parent);

        if (!string.IsNullOrWhiteSpace(lifecycleEvent.AccessToken))
        {
            await PublishAsync(new StreamingProxyChatLifecycleContinuationRequested
            {
                SessionId = request.SessionId,
                ScopeId = request.ScopeId,
                Prompt = request.Prompt,
                AccessToken = lifecycleEvent.AccessToken,
                PreferredRoute = lifecycleEvent.PreferredRoute,
                DefaultModel = lifecycleEvent.DefaultModel,
            }, TopologyAudience.Self);
        }

        Logger.LogInformation(
            "[StreamingProxy] Topic started: {Preview}",
            request.Prompt.Length > 100 ? request.Prompt[..100] + "..." : request.Prompt);
    }

    [EventHandler(EndpointName = "continueChatLifecycle", AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleChatLifecycleContinuationRequested(StreamingProxyChatLifecycleContinuationRequested request)
    {
        // Refactor (iter104/cluster-1): Old pattern: StreamingProxyChatLifecycleFacade owned chat continuation orchestration in Application layer. New principle: StreamingProxyGAgent typed continuation owns lifecycle; deprecated compat endpoints only normalize+dispatch typed command.
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.AccessToken))
            return;

        var coordinator = Services.GetRequiredService<StreamingProxyNyxParticipantCoordinator>();
        var participants = await coordinator.EnsureParticipantsJoinedAsync(
            request.ScopeId,
            Id,
            request.AccessToken,
            CancellationToken.None,
            request.PreferredRoute,
            request.DefaultModel);
        if (participants.Count == 0)
            return;

        var successfulReplies = await coordinator.GenerateRepliesAsync(
            participants,
            Id,
            request.Prompt,
            request.SessionId,
            request.AccessToken,
            CancellationToken.None);

        var terminalState = DetermineParticipantTerminalState(successfulReplies);
        await HandleSessionTerminalStateRequested(new StreamingProxySessionTerminalStateRequested
        {
            SessionId = request.SessionId,
            Status = terminalState.Status,
            ErrorMessage = terminalState.ErrorMessage ?? string.Empty,
        });
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
        };
        return next;
    }

    internal static (StreamingProxyChatSessionTerminalStatus Status, string? ErrorMessage) DetermineParticipantTerminalState(
        int successfulReplies) =>
        successfulReplies > 0
            ? (StreamingProxyChatSessionTerminalStatus.Completed, null)
            : (StreamingProxyChatSessionTerminalStatus.Failed, "StreamingProxy chat completed without any participant replies.");
}
