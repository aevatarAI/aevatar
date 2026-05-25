namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Query-side participant view for streaming proxy rooms.
/// </summary>
public interface IStreamingProxyParticipantQueryPort
{
    Task<IReadOnlyList<StreamingProxyParticipant>> ListAsync(
        string roomId, CancellationToken cancellationToken = default);
}

public sealed record StreamingProxyParticipant(
    string AgentId,
    string DisplayName,
    DateTimeOffset JoinedAt);
