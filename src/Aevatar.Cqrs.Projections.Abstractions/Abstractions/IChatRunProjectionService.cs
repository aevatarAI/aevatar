using Aevatar.Cqrs.Projections.Abstractions.ReadModels;

namespace Aevatar.Cqrs.Projections.Abstractions;

/// <summary>
/// Application-facing facade for chat run projection lifecycle.
/// </summary>
public interface IChatRunProjectionService
{
    bool ProjectionEnabled { get; }

    bool EnableRunQueryEndpoints { get; }

    bool EnableRunReportArtifacts { get; }

    Task<ChatRunProjectionSession> StartAsync(
        string rootActorId,
        string workflowName,
        string input,
        CancellationToken ct = default);

    Task ProjectAsync(
        ChatRunProjectionSession session,
        EventEnvelope envelope,
        CancellationToken ct = default);

    Task<bool> WaitForRunProjectionCompletedAsync(string runId, CancellationToken ct = default);

    Task<ChatRunReport?> CompleteAsync(
        ChatRunProjectionSession session,
        IReadOnlyList<ChatTopologyEdge> topology,
        CancellationToken ct = default);

    Task<IReadOnlyList<ChatRunReport>> ListRunsAsync(int take = 50, CancellationToken ct = default);

    Task<ChatRunReport?> GetRunAsync(string runId, CancellationToken ct = default);
}
