using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
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
public sealed class StreamingProxyGAgent : RoleGAgent
{
    public StreamingProxyGAgent(
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null)
        : base(llmProviderFactory, additionalHooks, agentMiddlewares, toolMiddlewares, llmMiddlewares, toolSources)
    {
    }

    private StreamingProxyGAgentState _proxyState = new();

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
    public new async Task HandleChatRequest(ChatRequestEvent request)
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
    /// Applies domain events to in-memory proxy state.
    /// Called by the event sourcing infrastructure after PersistDomainEventAsync.
    /// </summary>
    protected override RoleGAgentState TransitionState(RoleGAgentState current, IMessage evt)
    {
        // Let base handle its own events (InitializeRoleAgent, etc.)
        var baseResult = base.TransitionState(current, evt);

        // Also apply our proxy-specific events to _proxyState
        _proxyState = ApplyProxyEvent(_proxyState, evt);

        return baseResult;
    }

    private static StreamingProxyGAgentState ApplyProxyEvent(StreamingProxyGAgentState current, IMessage evt)
    {
        switch (evt)
        {
            case GroupChatRoomInitializedEvent init:
                var initState = current.Clone();
                initState.RoomName = init.RoomName;
                return initState;

            case GroupChatTopicEvent topic:
                var topicState = current.Clone();
                topicState.NextSequence++;
                topicState.Messages.Add(new StreamingProxyChatMessage
                {
                    Sequence = topicState.NextSequence,
                    SenderAgentId = "user",
                    SenderName = "User",
                    Content = topic.Prompt,
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    IsTopic = true,
                });
                TrimMessages(topicState);
                return topicState;

            case GroupChatMessageEvent msg:
                var msgState = current.Clone();
                msgState.NextSequence++;
                msgState.Messages.Add(new StreamingProxyChatMessage
                {
                    Sequence = msgState.NextSequence,
                    SenderAgentId = msg.AgentId,
                    SenderName = msg.AgentName,
                    Content = msg.Content,
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    IsTopic = false,
                });
                TrimMessages(msgState);
                return msgState;

            case GroupChatParticipantJoinedEvent joined:
                var joinState = current.Clone();
                // Remove existing entry if re-joining
                for (var i = joinState.Participants.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(joinState.Participants[i].AgentId, joined.AgentId, StringComparison.Ordinal))
                        joinState.Participants.RemoveAt(i);
                }
                joinState.Participants.Add(new StreamingProxyParticipant
                {
                    AgentId = joined.AgentId,
                    DisplayName = joined.DisplayName,
                    JoinedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                });
                return joinState;

            case GroupChatParticipantLeftEvent left:
                var leftState = current.Clone();
                for (var i = leftState.Participants.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(leftState.Participants[i].AgentId, left.AgentId, StringComparison.Ordinal))
                        leftState.Participants.RemoveAt(i);
                }
                return leftState;

            default:
                return current;
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
