namespace Aevatar.GAgents.StreamingProxy.Application.Rooms;

// Refactor (iter38/cluster-038-streaming-proxy-reuse-existing):
//   Old pattern: endpoints exposed room actions by composing actor lookup, raw envelopes, and dispatch locally.
//   New principle: callers use this existing Application command service for typed room create, post, join, and terminal-state commands.
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

    Task<StreamingProxyRoomLeaveResult> LeaveAsync(
        StreamingProxyRoomLeaveCommand command,
        CancellationToken cancellationToken = default);

    Task PublishTerminalStateAsync(
        StreamingProxyRoomTerminalStateCommand command,
        CancellationToken cancellationToken = default);

    Task SubmitParticipantsResolvedAsync(
        StreamingProxyRoomParticipantsResolvedCommand command,
        CancellationToken cancellationToken = default);

    Task SubmitParticipantReplyObservedAsync(
        StreamingProxyRoomParticipantReplyObservedCommand command,
        CancellationToken cancellationToken = default);

    Task SubmitParticipantReplyFailedAsync(
        StreamingProxyRoomParticipantReplyFailedCommand command,
        CancellationToken cancellationToken = default);
}

// Refactor (iter38/cluster-038-streaming-proxy-reuse-existing):
//   Old pattern: room create was the only typed command contract while related room actions stayed endpoint-local.
//   New principle: command, result, and status contracts describe the room command-service boundary explicitly.
public sealed record StreamingProxyRoomCreateCommand(
    string ScopeId,
    string? RoomName);

// Refactor (iter38/cluster-038-streaming-proxy-reuse-existing):
//   Old pattern: Streaming proxy endpoints built post-message room envelopes directly in Host code.
//   New principle: The Application command service owns typed message normalization and dispatch.
public sealed record StreamingProxyRoomPostMessageCommand(
    string RoomId,
    string AgentId,
    string? AgentName,
    string Content,
    string? SessionId);

// Refactor (iter38/cluster-038-streaming-proxy-reuse-existing):
//   Old pattern: Streaming proxy endpoints built join room envelopes and duplicated participant normalization in Host code.
//   New principle: The Application command service owns typed join normalization and returns the normalized participant identity.
public sealed record StreamingProxyRoomJoinCommand(
    string RoomId,
    string AgentId,
    string? DisplayName);

// Refactor (iter56/cluster-894-nyx-coordinator-adapter-only): old=coordinator-owned facts, new=adapter-only + room-actor-owned facts
// Leave is now a typed room command request, not direct construction of a committed leave event.
// Callers report participant lifecycle observations; StreamingProxyGAgent owns fact commitment.
// This keeps Nyx adapter behavior on the existing room command-service boundary.
public sealed record StreamingProxyRoomLeaveCommand(
    string RoomId,
    string AgentId,
    string? Reason);

// Refactor (iter38/cluster-038-streaming-proxy-reuse-existing):
//   Old pattern: Streaming proxy endpoints published terminal state envelopes through raw dispatch helpers.
//   New principle: The Application command service owns typed terminal-state publication without adding a second room interaction port.
public sealed record StreamingProxyRoomTerminalStateCommand(
    string RoomId,
    string SessionId,
    StreamingProxyChatSessionTerminalStatus Status,
    string? ErrorMessage);

public sealed record StreamingProxyRoomParticipantsResolvedCommand(
    string RoomId,
    string SessionId,
    IReadOnlyList<StreamingProxyChatLifecycleParticipant> Participants);

public sealed record StreamingProxyRoomParticipantReplyObservedCommand(
    string RoomId,
    string SessionId,
    string ParticipantId,
    int Round,
    int ParticipantIndex,
    string Content);

public sealed record StreamingProxyRoomParticipantReplyFailedCommand(
    string RoomId,
    string SessionId,
    string ParticipantId,
    int Round,
    int ParticipantIndex,
    StreamingProxyChatParticipantReplyFailureKind FailureKind,
    string? ErrorMessage);

public sealed record StreamingProxyRoomCreateResult(
    StreamingProxyRoomCreateStatus Status,
    string? RoomId,
    string? RoomName);

public sealed record StreamingProxyRoomPostMessageResult(
    StreamingProxyRoomPostMessageStatus Status);

public sealed record StreamingProxyRoomJoinResult(
    StreamingProxyRoomJoinStatus Status,
    string? AgentId,
    string? DisplayName);

public sealed record StreamingProxyRoomLeaveResult(
    StreamingProxyRoomLeaveStatus Status,
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
    Accepted = 0,
    RoomNotFound = 1,
}

public enum StreamingProxyRoomLeaveStatus
{
    Accepted = 0,
    RoomNotFound = 1,
}
