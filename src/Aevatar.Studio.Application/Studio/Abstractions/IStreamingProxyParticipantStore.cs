namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Read-only participant index for streaming proxy rooms.
/// </summary>
/// <remarks>
/// Refactor (iter43/issue-865-streaming-proxy-room-chat-host-orchestration):
///   Old pattern: StreamingProxy chat endpoint and participant coordinator fetch runtime actor objects, run Nyx participant discussion loops, mutate participant side-store state, and dispatch room events from Host/Application-side orchestration.
///   New principle: StreamingProxyGAgent owns participant admission, reply rounds, leave/failure decisions, and terminal-state publication; Host submits one typed command and observes projection/readmodel events only. Coordinator is adapter-only for Nyx external calls.
/// </remarks>
public interface IStreamingProxyParticipantStore
{
    Task<IReadOnlyList<StreamingProxyParticipant>> ListAsync(
        string roomId, CancellationToken cancellationToken = default);
}

public sealed record StreamingProxyParticipant(
    string AgentId,
    string DisplayName,
    DateTimeOffset JoinedAt);
