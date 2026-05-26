namespace Aevatar.GAgents.StreamingProxy.Application.Rooms;

// Refactor (iter50/issue-887-streaming-proxy-participant-authority):
//   Old pattern: StreamingProxyGAgent and singleton StreamingProxyParticipantGAgent both held participant fact; reads went to singleton readmodel, writes to both — dual fact source.
//   New principle: StreamingProxyGAgent per room is the single participant authority; singleton actor/store/readmodel deleted; reads go through room current-state projection.
internal interface IStreamingProxyRoomParticipantService
{
    Task<StreamingProxyRoomParticipantListResult> ListAsync(
        StreamingProxyRoomParticipantListQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StreamingProxyNyxParticipantDefinition>> EnsureNyxParticipantsJoinedAsync(
        StreamingProxyRoomNyxParticipantJoinCommand command,
        CancellationToken cancellationToken = default);

    Task<int> GenerateNyxRepliesAsync(
        StreamingProxyRoomNyxReplyCommand command,
        CancellationToken cancellationToken = default);
}

internal sealed record StreamingProxyRoomParticipantListQuery(
    string RoomId);

internal sealed record StreamingProxyRoomParticipantListResult(
    string RoomId,
    long StateVersion,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<StreamingProxyRoomParticipantEntry> Participants);

internal sealed record StreamingProxyRoomParticipantEntry(
    string AgentId,
    string DisplayName,
    DateTimeOffset JoinedAt);

internal sealed record StreamingProxyRoomNyxParticipantJoinCommand(
    string ScopeId,
    string RoomId,
    string AccessToken,
    string? PreferredRoute,
    string? DefaultModel);

internal sealed record StreamingProxyRoomNyxReplyCommand(
    string RoomId,
    string Prompt,
    string SessionId,
    string AccessToken,
    IReadOnlyList<StreamingProxyNyxParticipantDefinition> Participants);

internal sealed class StreamingProxyRoomNotFoundException : Exception
{
    public StreamingProxyRoomNotFoundException(string roomId)
        : base($"StreamingProxy room '{roomId}' was not found.")
    {
        RoomId = roomId;
    }

    public string RoomId { get; }
}
