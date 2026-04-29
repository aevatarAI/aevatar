using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.StreamingProxy.Application.Rooms;

public sealed class StreamingProxyRoomCommandService : IStreamingProxyRoomCommandService
{
    private const string DefaultRoomName = "Group Chat";

    private readonly IActorRuntime _actorRuntime;
    private readonly IGAgentActorRegistryCommandPort _registryCommandPort;
    private readonly ILogger<StreamingProxyRoomCommandService> _logger;

    public StreamingProxyRoomCommandService(
        IActorRuntime actorRuntime,
        IGAgentActorRegistryCommandPort registryCommandPort,
        ILogger<StreamingProxyRoomCommandService> logger)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
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
            await actor.HandleEventAsync(envelope, cancellationToken);

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

    private static EventEnvelope BuildRoomInitializedEnvelope(string actorId, string roomName)
    {
        var initEvent = new GroupChatRoomInitializedEvent { RoomName = roomName };
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(initEvent),
            Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = actorId } },
        };
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
