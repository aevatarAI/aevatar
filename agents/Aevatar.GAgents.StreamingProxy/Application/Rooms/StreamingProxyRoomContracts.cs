namespace Aevatar.GAgents.StreamingProxy.Application.Rooms;

public interface IStreamingProxyRoomCommandService
{
    Task<StreamingProxyRoomCreateResult> CreateRoomAsync(
        StreamingProxyRoomCreateCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record StreamingProxyRoomCreateCommand(
    string ScopeId,
    string? RoomName);

public sealed record StreamingProxyRoomCreateResult(
    StreamingProxyRoomCreateStatus Status,
    string? RoomId,
    string? RoomName);

public enum StreamingProxyRoomCreateStatus
{
    Created = 0,
    AdmissionUnavailable = 1,
    Failed = 2,
}
