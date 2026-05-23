using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.StreamingProxy.Application.Rooms;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.StreamingProxy;

public sealed record StreamingProxyChatLifecycleRequest(
    string ScopeId,
    string RoomId,
    string Prompt,
    string SessionId,
    string? AccessToken,
    string? PreferredRoute,
    string? DefaultModel);

public sealed record StreamingProxyJoinLifecycleReceipt(
    StreamingProxyJoinLifecycleStatus Status,
    string? AgentId);

public enum StreamingProxyJoinLifecycleStatus
{
    Joined = 0,
    RoomNotFound = 1,
}

public enum StreamingProxyRoomDeleteLifecycleStatus
{
    Accepted = 0,
    Failed = 1,
}

public sealed record StreamingProxySubscriptionLifecycleReceipt(
    StreamingProxySubscriptionLifecycleStatus Status,
    StreamingProxyRoomSubscriptionObservationAttachment? Attachment);

public enum StreamingProxySubscriptionLifecycleStatus
{
    Attached = 0,
    RoomNotFound = 1,
    ProjectionUnavailable = 2,
}

// Refactor (iter47/issue-877-chat-endpoints-own-lifecycle-and-compensation):
//   Old pattern: Chat endpoints owned actor lifecycle, registry compensation, participant orchestration, terminal-state recovery, and IChatHistoryStore side effects.
//   New principle: Endpoint is adapter-only (HTTP/SSE); typed command facade owns lifecycle; existing chat actors own compensation events and terminal-state publication.
internal sealed class StreamingProxyChatLifecycleFacade
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IStreamingProxyRoomCommandService _roomCommandService;
    private readonly IStreamingProxyRoomParticipantService _participantService;
    private readonly ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus> _interactionService;
    private readonly IGAgentActorRegistryCommandPort _registryCommandPort;
    private readonly StreamingProxyChatDurableCompletionResolver _durableCompletionResolver;
    private readonly IStreamingProxyRoomSubscriptionObservationPort _subscriptionObservationPort;
    private readonly ILogger<StreamingProxyChatLifecycleFacade> _logger;

    public StreamingProxyChatLifecycleFacade(
        IActorRuntime actorRuntime,
        IStreamingProxyRoomCommandService roomCommandService,
        IStreamingProxyRoomParticipantService participantService,
        ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus> interactionService,
        IGAgentActorRegistryCommandPort registryCommandPort,
        StreamingProxyChatDurableCompletionResolver durableCompletionResolver,
        IStreamingProxyRoomSubscriptionObservationPort subscriptionObservationPort,
        ILogger<StreamingProxyChatLifecycleFacade> logger)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _roomCommandService = roomCommandService ?? throw new ArgumentNullException(nameof(roomCommandService));
        _participantService = participantService ?? throw new ArgumentNullException(nameof(participantService));
        _interactionService = interactionService ?? throw new ArgumentNullException(nameof(interactionService));
        _registryCommandPort = registryCommandPort ?? throw new ArgumentNullException(nameof(registryCommandPort));
        _durableCompletionResolver = durableCompletionResolver ?? throw new ArgumentNullException(nameof(durableCompletionResolver));
        _subscriptionObservationPort = subscriptionObservationPort ?? throw new ArgumentNullException(nameof(subscriptionObservationPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CommandInteractionResult<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyProjectionCompletionStatus>> RunChatAsync(
        StreamingProxyChatLifecycleRequest request,
        Func<StreamingProxyRoomSessionEnvelope, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(emitAsync);

        string? acceptedRoomId = null;
        try
        {
            return await _interactionService.ExecuteAsync(
                new StreamingProxyRoomChatCommand(
                    request.RoomId,
                    request.ScopeId,
                    request.Prompt,
                    request.SessionId,
                    request.AccessToken,
                    request.PreferredRoute,
                    request.DefaultModel),
                emitAsync,
                async (receipt, token) =>
                {
                    acceptedRoomId = receipt.ActorId;
                    await ContinueParticipantLifecycleAsync(request, receipt.ActorId, token);
                },
                ct);
        }
        catch (OperationCanceledException)
        {
            await TryPublishTerminalStateAsync(
                acceptedRoomId,
                request.SessionId,
                StreamingProxyChatSessionTerminalStatus.Failed,
                "StreamingProxy chat was cancelled before completion.",
                CancellationToken.None);
            throw;
        }
        catch
        {
            await TryPublishTerminalStateAsync(
                acceptedRoomId,
                request.SessionId,
                StreamingProxyChatSessionTerminalStatus.Failed,
                "StreamingProxy chat failed before completion.",
                CancellationToken.None);
            throw;
        }
    }

    public async Task<StreamingProxyJoinLifecycleReceipt> JoinAsync(
        string roomId,
        string agentId,
        string? displayName,
        CancellationToken ct = default)
    {
        var result = await _roomCommandService.JoinAsync(
            new StreamingProxyRoomJoinCommand(roomId, agentId, displayName),
            ct);
        if (result.Status == StreamingProxyRoomJoinStatus.RoomNotFound)
            return new StreamingProxyJoinLifecycleReceipt(StreamingProxyJoinLifecycleStatus.RoomNotFound, null);

        var normalizedAgentId = result.AgentId ?? agentId.Trim();
        return new StreamingProxyJoinLifecycleReceipt(
            StreamingProxyJoinLifecycleStatus.Joined,
            normalizedAgentId);
    }

    public async Task<StreamingProxyRoomDeleteLifecycleStatus> DeleteRoomAsync(
        string scopeId,
        string roomId,
        CancellationToken ct = default)
    {
        try
        {
            await _registryCommandPort.UnregisterActorAsync(
                new GAgentActorRegistration(
                    scopeId,
                    StreamingProxyDefaults.GAgentTypeName,
                    roomId),
                ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete streaming proxy room {RoomId}", roomId);
            return StreamingProxyRoomDeleteLifecycleStatus.Failed;
        }

        return StreamingProxyRoomDeleteLifecycleStatus.Accepted;
    }

    public async Task<StreamingProxyRoomParticipantListResult> ListParticipantsAsync(
        string roomId,
        CancellationToken ct = default) =>
        await _participantService.ListAsync(new StreamingProxyRoomParticipantListQuery(roomId), ct);

    public async Task<StreamingProxySubscriptionLifecycleReceipt> AttachSubscriptionAsync(
        string roomId,
        IEventSink<StreamingProxyRoomSessionEnvelope> sink,
        CancellationToken ct = default)
    {
        var actor = await _actorRuntime.GetAsync(roomId);
        if (actor is null)
            return new StreamingProxySubscriptionLifecycleReceipt(
                StreamingProxySubscriptionLifecycleStatus.RoomNotFound,
                null);

        var attachment = await _subscriptionObservationPort.AttachAsync(roomId, sink, ct);
        return attachment is null
            ? new StreamingProxySubscriptionLifecycleReceipt(
                StreamingProxySubscriptionLifecycleStatus.ProjectionUnavailable,
                null)
            : new StreamingProxySubscriptionLifecycleReceipt(
                StreamingProxySubscriptionLifecycleStatus.Attached,
                attachment);
    }

    public Task DetachSubscriptionAsync(
        StreamingProxyRoomSubscriptionObservationAttachment attachment,
        IEventSink<StreamingProxyRoomSessionEnvelope> sink,
        CancellationToken ct = default) =>
        _subscriptionObservationPort.DetachAndDisposeAsync(attachment, sink, ct);

    private async Task ContinueParticipantLifecycleAsync(
        StreamingProxyChatLifecycleRequest request,
        string acceptedRoomId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.AccessToken))
            return;

        var participants = await _participantService.EnsureNyxParticipantsJoinedAsync(
            new StreamingProxyRoomNyxParticipantJoinCommand(
                request.ScopeId,
                acceptedRoomId,
                request.AccessToken,
                request.PreferredRoute,
                request.DefaultModel),
            ct);
        if (participants.Count == 0)
            return;

        var successfulReplies = await _participantService.GenerateNyxRepliesAsync(
            new StreamingProxyRoomNyxReplyCommand(
                acceptedRoomId,
                request.Prompt,
                request.SessionId,
                request.AccessToken,
                participants),
            ct);

        var terminalState = DetermineParticipantTerminalState(successfulReplies);
        await _roomCommandService.PublishTerminalStateAsync(
            new StreamingProxyRoomTerminalStateCommand(
                acceptedRoomId,
                request.SessionId,
                terminalState.Status,
                terminalState.ErrorMessage),
            ct);
    }

    private async Task TryPublishTerminalStateAsync(
        string? roomId,
        string? sessionId,
        StreamingProxyChatSessionTerminalStatus status,
        string errorMessage,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(sessionId))
            return;

        try
        {
            var durableCompletion = await _durableCompletionResolver.ResolveAsync(roomId, sessionId, ct);
            if (durableCompletion is StreamingProxyProjectionCompletionStatus.Completed or StreamingProxyProjectionCompletionStatus.Failed)
                return;

            await _roomCommandService.PublishTerminalStateAsync(
                new StreamingProxyRoomTerminalStateCommand(roomId, sessionId, status, errorMessage),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish terminal fallback state for room {RoomId}, session {SessionId}",
                roomId,
                sessionId);
        }
    }

    internal static (StreamingProxyChatSessionTerminalStatus Status, string? ErrorMessage) DetermineParticipantTerminalState(
        int successfulReplies) =>
        successfulReplies > 0
            ? (StreamingProxyChatSessionTerminalStatus.Completed, null)
            : (StreamingProxyChatSessionTerminalStatus.Failed, "StreamingProxy chat completed without any participant replies.");
}
