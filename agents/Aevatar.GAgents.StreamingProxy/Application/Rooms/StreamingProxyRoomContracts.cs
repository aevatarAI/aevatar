namespace Aevatar.GAgents.StreamingProxy.Application.Rooms;

public interface IStreamingProxyRoomCommandService
{
    Task<StreamingProxyRoomCreateResult> CreateRoomAsync(
        StreamingProxyRoomCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<StreamingProxyRoomPostMessageResult> PostMessageAsync(
        StreamingProxyRoomPostMessageCommand command,
        CancellationToken cancellationToken = default);

    Task<StreamingProxyRoomJoinResult> JoinAsync(
        StreamingProxyRoomJoinCommand command,
        CancellationToken cancellationToken = default);

    Task PublishTerminalStateAsync(
        StreamingProxyRoomTerminalStateCommand command,
        CancellationToken cancellationToken = default);
}

// Refactor (iter38/cluster-038-streaming-proxy-reuse-existing):
//   Old pattern: Streaming proxy endpoint orchestration:Host endpoints do platform selection / scope resolution / post-message / join / terminal directly with raw runtime/dispatch helpers + 无 typed Application port。
//   New principle: Extend existing IStreamingProxyRoomCommandService with narrow typed post-message/join/terminal-state publication methods。Preserve command lifecycle semantics internally。**禁止** new IStreamingProxyRoomInteractionPort / 新 actor / 新 envelope / full CQRS skeleton。
public sealed record StreamingProxyRoomCreateCommand(
    string ScopeId,
    string? RoomName);

// refactor helper, no behavior change
public sealed record StreamingProxyRoomPostMessageCommand(
    string RoomId,
    string AgentId,
    string? AgentName,
    string Content,
    string? SessionId);

// refactor helper, no behavior change
public sealed record StreamingProxyRoomJoinCommand(
    string RoomId,
    string AgentId,
    string? DisplayName);

// refactor helper, no behavior change
public sealed record StreamingProxyRoomTerminalStateCommand(
    string RoomId,
    string SessionId,
    StreamingProxyChatSessionTerminalStatus Status,
    string? ErrorMessage);

public sealed record StreamingProxyRoomCreateResult(
    StreamingProxyRoomCreateStatus Status,
    string? RoomId,
    string? RoomName);

public sealed record StreamingProxyRoomPostMessageResult(
    StreamingProxyRoomPostMessageStatus Status);

public sealed record StreamingProxyRoomJoinResult(
    StreamingProxyRoomJoinStatus Status,
    string? AgentId);

public enum StreamingProxyRoomCreateStatus
{
    Created = 0,
    AdmissionUnavailable = 1,
    Failed = 2,
}

public enum StreamingProxyRoomPostMessageStatus
{
    Accepted = 0,
    RoomNotFound = 1,
}

public enum StreamingProxyRoomJoinStatus
{
    Joined = 0,
    RoomNotFound = 1,
}
