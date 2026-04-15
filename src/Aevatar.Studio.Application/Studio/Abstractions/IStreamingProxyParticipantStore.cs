namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Persistent participant index for streaming proxy rooms.
/// </summary>
public interface IStreamingProxyParticipantStore
{
    Task<IReadOnlyList<StreamingProxyParticipant>> ListAsync(
        string roomId, CancellationToken cancellationToken = default);

    Task AddAsync(
        string roomId, string agentId, string displayName,
        CancellationToken cancellationToken = default);

    Task RemoveParticipantAsync(
        string roomId, string agentId,
        CancellationToken cancellationToken = default);

    Task RemoveRoomAsync(
        string roomId, CancellationToken cancellationToken = default);
}

public sealed record StreamingProxyParticipant(
    string AgentId,
    string DisplayName,
    DateTimeOffset JoinedAt);
