using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

// Refactor (iter17/cluster-034):
//   Old pattern: Replay-based projection scope watermark query via event-store reads.
//   New principle: Materialized ProjectionScopeStatusDocument readmodel; ProjectionScopeStatusQueryPort reads document only; never replays event streams.
public sealed class ProjectionScopeStatusQueryPort : IProjectionScopeWatermarkQueryPort
{
    private readonly IProjectionDocumentReader<ProjectionScopeStatusDocument, string> _documentReader;

    public ProjectionScopeStatusQueryPort(
        IProjectionDocumentReader<ProjectionScopeStatusDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<long?> GetLastSuccessfulVersionAsync(
        ProjectionRuntimeScopeKey scopeKey,
        CancellationToken ct = default)
    {
        // Query ports read materialized projection-scope status; they never replay event streams.
        var scopeActorId = ProjectionScopeActorId.Build(scopeKey);
        var document = await _documentReader.GetAsync(scopeActorId, ct);
        return document is { Active: true, Released: false }
            ? document.LastSuccessfulVersion
            : null;
    }
}
