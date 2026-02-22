namespace Aevatar.Demos.CaseProjection.Abstractions;

public interface ICaseProjectionService
{
    bool ProjectionEnabled { get; }

    bool EnableRunQueryEndpoints { get; }

    bool EnableRunReportArtifacts { get; }

    Task<CaseProjectionSession> StartAsync(
        string rootActorId,
        string caseId,
        string caseType,
        string input,
        CancellationToken ct = default);

    Task ProjectAsync(
        CaseProjectionSession session,
        EventEnvelope envelope,
        CancellationToken ct = default);

    Task<CaseProjectionReadModel?> CompleteAsync(
        CaseProjectionSession session,
        IReadOnlyList<CaseTopologyEdge> topology,
        CancellationToken ct = default);

    Task<IReadOnlyList<CaseProjectionReadModel>> ListRunsAsync(int take = 50, CancellationToken ct = default);

    Task<CaseProjectionReadModel?> GetRunAsync(string runId, CancellationToken ct = default);
}
