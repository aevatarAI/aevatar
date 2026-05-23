using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
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
    private readonly IStreamingProxyRoomCommandService _roomCommandService;
    private readonly ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus> _interactionService;
    private readonly IStreamingProxyParticipantStore _participantStore;
    private readonly IStreamingProxyRoomSubscriptionObservationPort _subscriptionObservationPort;
    private readonly ILogger<StreamingProxyChatLifecycleFacade> _logger;

    public StreamingProxyChatLifecycleFacade(
        IActorRuntime actorRuntime,
        IStreamingProxyRoomCommandService roomCommandService,
        ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus> interactionService,
        IStreamingProxyParticipantStore participantStore,
        IStreamingProxyRoomSubscriptionObservationPort subscriptionObservationPort,
        ILogger<StreamingProxyChatLifecycleFacade> logger)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _roomCommandService = roomCommandService ?? throw new ArgumentNullException(nameof(roomCommandService));
        _interactionService = interactionService ?? throw new ArgumentNullException(nameof(interactionService));
        _participantStore = participantStore ?? throw new ArgumentNullException(nameof(participantStore));
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
            null,
            ct);
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

}
