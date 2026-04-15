namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Persistent participant index for streaming proxy rooms.
/// </summary>
/// <remarks>
/// TODO: When wiring to endpoints, the caller must handle corrupt-data exceptions
/// from the underlying store (e.g. <see cref="InvalidOperationException"/> from
/// deserialization failures). Swallowing errors and returning an empty list would
/// silently discard existing participants. The chrono-storage implementation
/// intentionally throws on corruption to prevent data loss.
/// </remarks>
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
