using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.StreamingProxy.Application.Rooms;
using Aevatar.Studio.Application.Studio.Abstractions;
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
    private readonly IGAgentActorRegistryCommandPort _registryCommandPort;
    private readonly IStreamingProxyRoomCommandService _roomCommandService;
    private readonly ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus> _interactionService;
    private readonly StreamingProxyChatDurableCompletionResolver _durableCompletionResolver;
    private readonly IStreamingProxyParticipantStore _participantStore;
    private readonly StreamingProxyNyxParticipantCoordinator _participantCoordinator;
    private readonly IStreamingProxyRoomSubscriptionObservationPort _subscriptionObservationPort;
    private readonly ILogger<StreamingProxyChatLifecycleFacade> _logger;

    public StreamingProxyChatLifecycleFacade(
        IActorRuntime actorRuntime,
        IGAgentActorRegistryCommandPort registryCommandPort,
        IStreamingProxyRoomCommandService roomCommandService,
        ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus> interactionService,
        StreamingProxyChatDurableCompletionResolver durableCompletionResolver,
        IStreamingProxyParticipantStore participantStore,
        StreamingProxyNyxParticipantCoordinator participantCoordinator,
        IStreamingProxyRoomSubscriptionObservationPort subscriptionObservationPort,
        ILogger<StreamingProxyChatLifecycleFacade> logger)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _registryCommandPort = registryCommandPort ?? throw new ArgumentNullException(nameof(registryCommandPort));
        _roomCommandService = roomCommandService ?? throw new ArgumentNullException(nameof(roomCommandService));
        _interactionService = interactionService ?? throw new ArgumentNullException(nameof(interactionService));
        _durableCompletionResolver = durableCompletionResolver ?? throw new ArgumentNullException(nameof(durableCompletionResolver));
        _participantStore = participantStore ?? throw new ArgumentNullException(nameof(participantStore));
        _participantCoordinator = participantCoordinator ?? throw new ArgumentNullException(nameof(participantCoordinator));
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

        IActor? actor = null;
        try
        {
            actor = await _actorRuntime.GetAsync(request.RoomId);
            if (actor is null)
            {
                return CommandInteractionResult<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyProjectionCompletionStatus>
                    .Failure(StreamingProxyRoomChatStartError.RoomNotFound);
            }

            return await _interactionService.ExecuteAsync(
                new StreamingProxyRoomChatCommand(
                    request.RoomId,
                    request.ScopeId,
                    request.Prompt,
                    request.SessionId),
                emitAsync,
                async (_, token) => await CoordinateParticipantsAndPublishTerminalAsync(request, actor, token),
                ct);
        }
        catch (OperationCanceledException)
        {
            await TryPublishCanceledTerminalStateAsync(actor, request.SessionId);
            throw;
        }
        catch
        {
            await TryPublishFailedTerminalStateAsync(
                actor,
                request.SessionId,
                "StreamingProxy chat failed before completion.");
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
        var normalizedDisplayName = result.DisplayName ?? normalizedAgentId;
        try
        {
            await _participantStore.AddAsync(roomId, normalizedAgentId, normalizedDisplayName, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist participant {AgentId} in room {RoomId}", normalizedAgentId, roomId);
        }

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
                new GAgentActorRegistration(scopeId, StreamingProxyDefaults.GAgentTypeName, roomId),
                ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unregister room {RoomId} from registry", roomId);
            return StreamingProxyRoomDeleteLifecycleStatus.Failed;
        }

        try
        {
            await _participantStore.RemoveRoomAsync(roomId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove participants for room {RoomId}", roomId);
        }

        return StreamingProxyRoomDeleteLifecycleStatus.Accepted;
    }

    public Task<IReadOnlyList<Aevatar.Studio.Application.Studio.Abstractions.StreamingProxyParticipant>> ListParticipantsAsync(
        string roomId,
        CancellationToken ct = default) =>
        _participantStore.ListAsync(roomId, ct);

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

    public Task PublishFailedTerminalStateAsync(
        string roomId,
        string sessionId,
        string errorMessage,
        CancellationToken ct = default) =>
        _roomCommandService.PublishTerminalStateAsync(
            new StreamingProxyRoomTerminalStateCommand(
                roomId,
                sessionId,
                StreamingProxyChatSessionTerminalStatus.Failed,
                errorMessage),
            ct);

    private async Task CoordinateParticipantsAndPublishTerminalAsync(
        StreamingProxyChatLifecycleRequest request,
        IActor actor,
        CancellationToken ct)
    {
        IReadOnlyList<StreamingProxyNyxParticipantDefinition> participants =
            string.IsNullOrWhiteSpace(request.AccessToken)
                ? Array.Empty<StreamingProxyNyxParticipantDefinition>()
                : await _participantCoordinator.EnsureParticipantsJoinedAsync(
                    request.ScopeId,
                    request.RoomId,
                    actor,
                    _participantStore,
                    request.AccessToken,
                    ct,
                    request.PreferredRoute,
                    request.DefaultModel);

        if (participants.Count == 0 || string.IsNullOrWhiteSpace(request.AccessToken))
            return;

        var successfulReplies = await _participantCoordinator.GenerateRepliesAsync(
            participants,
            actor,
            request.Prompt,
            request.SessionId,
            request.AccessToken,
            ct,
            _participantStore,
            request.RoomId);
        var terminalState = DetermineParticipantTerminalState(successfulReplies);
        await _roomCommandService.PublishTerminalStateAsync(
            new StreamingProxyRoomTerminalStateCommand(
                actor.Id,
                request.SessionId,
                terminalState.Status,
                terminalState.ErrorMessage),
            ct);
    }

    private async Task TryPublishCanceledTerminalStateAsync(
        IActor? actor,
        string? sessionId)
    {
        if (actor is null || string.IsNullOrWhiteSpace(sessionId))
            return;

        try
        {
            var durableCompletion = await _durableCompletionResolver.ResolveAsync(actor.Id, sessionId, CancellationToken.None);
            if (durableCompletion is StreamingProxyProjectionCompletionStatus.Completed or StreamingProxyProjectionCompletionStatus.Failed)
                return;

            await PublishFailedTerminalStateAsync(
                actor.Id,
                sessionId,
                "StreamingProxy chat was cancelled before completion.",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish terminal cancellation state for room {RoomId}, session {SessionId}",
                actor.Id,
                sessionId);
        }
    }

    private async Task TryPublishFailedTerminalStateAsync(
        IActor? actor,
        string? sessionId,
        string errorMessage)
    {
        if (actor is null || string.IsNullOrWhiteSpace(sessionId))
            return;

        try
        {
            var durableCompletion = await _durableCompletionResolver.ResolveAsync(actor.Id, sessionId, CancellationToken.None);
            if (durableCompletion is StreamingProxyProjectionCompletionStatus.Completed or StreamingProxyProjectionCompletionStatus.Failed)
                return;

            await PublishFailedTerminalStateAsync(actor.Id, sessionId, errorMessage, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish terminal failure state for room {RoomId}, session {SessionId}",
                actor.Id,
                sessionId);
        }
    }

    internal static (StreamingProxyChatSessionTerminalStatus Status, string? ErrorMessage) DetermineParticipantTerminalState(
        int successfulReplies) =>
        successfulReplies > 0
            ? (StreamingProxyChatSessionTerminalStatus.Completed, null)
            : (StreamingProxyChatSessionTerminalStatus.Failed, "StreamingProxy chat completed without any participant replies.");
}
