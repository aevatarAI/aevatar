using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.StreamingProxy.Application.Rooms;

// Refactor (iter38/cluster-038-streaming-proxy-reuse-existing):
//   Old pattern: Streaming proxy endpoint orchestration:Host endpoints do platform selection / scope resolution / post-message / join / terminal directly with raw runtime/dispatch helpers + 无 typed Application port。
//   New principle: Extend existing IStreamingProxyRoomCommandService with narrow typed post-message/join/terminal-state publication methods。Preserve command lifecycle semantics internally。**禁止** new IStreamingProxyRoomInteractionPort / 新 actor / 新 envelope / full CQRS skeleton。
public sealed class StreamingProxyRoomCommandService : IStreamingProxyRoomCommandService
{
    private const string DefaultRoomName = "Group Chat";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly IGAgentActorRegistryCommandPort _registryCommandPort;
    private readonly ILogger<StreamingProxyRoomCommandService> _logger;

    public StreamingProxyRoomCommandService(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        IGAgentActorRegistryCommandPort registryCommandPort,
        ILogger<StreamingProxyRoomCommandService> logger)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _registryCommandPort = registryCommandPort ?? throw new ArgumentNullException(nameof(registryCommandPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<StreamingProxyRoomCreateResult> CreateRoomAsync(
        StreamingProxyRoomCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var scopeId = NormalizeRequiredScopeId(command.ScopeId);
        var roomName = NormalizeRoomName(command.RoomName);
        var roomId = StreamingProxyDefaults.GenerateRoomId();
        var targetCreated = false;

        try
        {
            var actor = await _actorRuntime.CreateAsync<StreamingProxyGAgent>(roomId, cancellationToken);
            targetCreated = true;

            var envelope = BuildRoomInitializedEnvelope(actor.Id, roomName);
            await DispatchRoomEnvelopeAsync(actor.Id, envelope, cancellationToken);

            var receipt = await _registryCommandPort.RegisterActorAsync(
                new GAgentActorRegistration(scopeId, StreamingProxyDefaults.GAgentTypeName, roomId),
                cancellationToken);
            if (!receipt.IsAdmissionVisible)
            {
                await TryRollbackRoomCreationAsync(scopeId, roomId, CancellationToken.None);
                return new StreamingProxyRoomCreateResult(
                    StreamingProxyRoomCreateStatus.AdmissionUnavailable,
                    roomId,
                    roomName);
            }

            return new StreamingProxyRoomCreateResult(
                StreamingProxyRoomCreateStatus.Created,
                roomId,
                roomName);
        }
        catch (OperationCanceledException)
        {
            if (targetCreated)
                await TryRollbackRoomCreationAsync(scopeId, roomId, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create streaming proxy room {RoomId}", roomId);
            if (targetCreated)
                await TryRollbackRoomCreationAsync(scopeId, roomId, CancellationToken.None);
            return new StreamingProxyRoomCreateResult(
                StreamingProxyRoomCreateStatus.Failed,
                roomId,
                roomName);
        }
    }

    public async Task<StreamingProxyRoomPostMessageResult> PostMessageAsync(
        StreamingProxyRoomPostMessageCommand command,
        CancellationToken cancellationToken = default)
    {
        // Refactor (iter38/cluster-038-streaming-proxy-reuse-existing):
        //   Old pattern: Streaming proxy endpoint orchestration:Host endpoints do platform selection / scope resolution / post-message / join / terminal directly with raw runtime/dispatch helpers + 无 typed Application port。
        //   New principle: Extend existing IStreamingProxyRoomCommandService with narrow typed post-message/join/terminal-state publication methods。Preserve command lifecycle semantics internally。**禁止** new IStreamingProxyRoomInteractionPort / 新 actor / 新 envelope / full CQRS skeleton。
        ArgumentNullException.ThrowIfNull(command);

        var actor = await _actorRuntime.GetAsync(command.RoomId.Trim());
        if (actor is null)
            return new StreamingProxyRoomPostMessageResult(StreamingProxyRoomPostMessageStatus.RoomNotFound);

        var agentId = NormalizeRequiredValue(command.AgentId, nameof(command.AgentId));
        var envelope = BuildRoomEnvelope(
            actor.Id,
            new GroupChatMessageEvent
            {
                AgentId = agentId,
                AgentName = NormalizeOptionalValue(command.AgentName) ?? agentId,
                Content = NormalizeRequiredValue(command.Content, nameof(command.Content)),
                SessionId = NormalizeOptionalValue(command.SessionId) ?? Guid.NewGuid().ToString("N"),
            });

        await DispatchRoomEnvelopeAsync(actor.Id, envelope, cancellationToken);
        return new StreamingProxyRoomPostMessageResult(StreamingProxyRoomPostMessageStatus.Accepted);
    }

    public async Task<StreamingProxyRoomJoinResult> JoinAsync(
        StreamingProxyRoomJoinCommand command,
        CancellationToken cancellationToken = default)
    {
        // Refactor (iter38/cluster-038-streaming-proxy-reuse-existing):
        //   Old pattern: Streaming proxy endpoint orchestration:Host endpoints do platform selection / scope resolution / post-message / join / terminal directly with raw runtime/dispatch helpers + 无 typed Application port。
        //   New principle: Extend existing IStreamingProxyRoomCommandService with narrow typed post-message/join/terminal-state publication methods。Preserve command lifecycle semantics internally。**禁止** new IStreamingProxyRoomInteractionPort / 新 actor / 新 envelope / full CQRS skeleton。
        ArgumentNullException.ThrowIfNull(command);

        var actor = await _actorRuntime.GetAsync(command.RoomId.Trim());
        if (actor is null)
            return new StreamingProxyRoomJoinResult(StreamingProxyRoomJoinStatus.RoomNotFound, null, null);

        var agentId = NormalizeRequiredValue(command.AgentId, nameof(command.AgentId));
        var displayName = NormalizeOptionalValue(command.DisplayName) ?? agentId;
        var envelope = BuildRoomEnvelope(
            actor.Id,
            new GroupChatParticipantJoinedEvent
            {
                AgentId = agentId,
                DisplayName = displayName,
            });

        await DispatchRoomEnvelopeAsync(actor.Id, envelope, cancellationToken);
        return new StreamingProxyRoomJoinResult(StreamingProxyRoomJoinStatus.Joined, agentId, displayName);
    }

    public Task PublishTerminalStateAsync(
        StreamingProxyRoomTerminalStateCommand command,
        CancellationToken cancellationToken = default)
    {
        // Refactor (iter38/cluster-038-streaming-proxy-reuse-existing):
        //   Old pattern: Streaming proxy endpoint orchestration:Host endpoints do platform selection / scope resolution / post-message / join / terminal directly with raw runtime/dispatch helpers + 无 typed Application port。
        //   New principle: Extend existing IStreamingProxyRoomCommandService with narrow typed post-message/join/terminal-state publication methods。Preserve command lifecycle semantics internally。**禁止** new IStreamingProxyRoomInteractionPort / 新 actor / 新 envelope / full CQRS skeleton。
        ArgumentNullException.ThrowIfNull(command);

        var roomId = NormalizeRequiredValue(command.RoomId, nameof(command.RoomId));
        var envelope = BuildRoomEnvelope(
            roomId,
            new StreamingProxyChatSessionTerminalStateChanged
            {
                SessionId = NormalizeRequiredValue(command.SessionId, nameof(command.SessionId)),
                Status = command.Status,
                TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                ErrorMessage = command.ErrorMessage ?? string.Empty,
            });

        return DispatchRoomEnvelopeAsync(roomId, envelope, cancellationToken);
    }

    private static string NormalizeRequiredScopeId(string? scopeId)
    {
        var normalized = scopeId?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("ScopeId is required.", nameof(StreamingProxyRoomCreateCommand.ScopeId));

        return normalized;
    }

    private static string NormalizeRoomName(string? roomName)
    {
        var normalized = roomName?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? DefaultRoomName : normalized;
    }

    private static string NormalizeRequiredValue(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{parameterName} is required.", parameterName);

        return normalized;
    }

    private static string? NormalizeOptionalValue(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static EventEnvelope BuildRoomInitializedEnvelope(string actorId, string roomName)
    {
        var initEvent = new GroupChatRoomInitializedEvent { RoomName = roomName };
        return BuildRoomEnvelope(actorId, initEvent);
    }

    private static EventEnvelope BuildRoomEnvelope(string actorId, IMessage payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = actorId } },
        };
    }

    private Task DispatchRoomEnvelopeAsync(
        string actorId,
        EventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        // Refactor (iter4/cluster-008):
        //   Old pattern: room creation invoked the actor event handler inline after actor creation.
        //   New principle: application services deliver room commands through IActorDispatchPort.
        return _actorDispatchPort.DispatchAsync(actorId, envelope, cancellationToken);
    }

    private async Task TryRollbackRoomCreationAsync(
        string scopeId,
        string roomId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _registryCommandPort.UnregisterActorAsync(
                new GAgentActorRegistration(
                    scopeId,
                    StreamingProxyDefaults.GAgentTypeName,
                    roomId),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unregister room {RoomId} from registry during rollback", roomId);
            return;
        }

        try
        {
            await _actorRuntime.DestroyAsync(roomId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to destroy room actor {RoomId} during rollback", roomId);
        }
    }
}
