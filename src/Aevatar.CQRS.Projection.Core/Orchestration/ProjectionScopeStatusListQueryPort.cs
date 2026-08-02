using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

// Read-model LIST query port for projection-scope statuses (companion to ProjectionScopeStatusQueryPort,
// which reads a single scope's watermark).
//
// Query path reads the materialized ProjectionScopeStatusDocument read-model ONLY via the document
// reader; it never replays event streams, rebuilds state, or touches IEventStore.
public sealed class ProjectionScopeStatusListQueryPort : IProjectionScopeStatusListQueryPort
{
    // Hard cap on the page size regardless of the requested take, so a list read can never fan out
    // unbounded over the read-model store.
    private const int MaxTake = 200;

    private readonly IProjectionDocumentReader<ProjectionScopeStatusDocument, string> _documentReader;
    private readonly ILogger<ProjectionScopeStatusListQueryPort> _logger;

    public ProjectionScopeStatusListQueryPort(
        IProjectionDocumentReader<ProjectionScopeStatusDocument, string> documentReader,
        ILogger<ProjectionScopeStatusListQueryPort>? logger = null)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _logger = logger ?? NullLogger<ProjectionScopeStatusListQueryPort>.Instance;
    }

    public async Task<IReadOnlyList<ProjectionScopeStatusSnapshot>> ListAsync(
        ProjectionScopeStatusListQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var boundedTake = Math.Clamp(query.Take <= 0 ? MaxTake : query.Take, 1, MaxTake);

        // Query ports read the materialized projection-scope status read-model; they never replay
        // event streams. Newest-updated first, with the document id as a stable tiebreaker.
        var result = await _documentReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = boundedTake,
                IncludeTotalCount = true,
                Sorts =
                [
                    new ProjectionDocumentSort
                    {
                        FieldPath = nameof(ProjectionScopeStatusDocument.UpdatedAt),
                        Direction = ProjectionDocumentSortDirection.Desc,
                    },
                    new ProjectionDocumentSort
                    {
                        FieldPath = nameof(ProjectionScopeStatusDocument.Id),
                        Direction = ProjectionDocumentSortDirection.Asc,
                    },
                ],
            },
            ct);

        if (result.TotalCount is { } totalCount && totalCount > result.Items.Count)
        {
            _logger.LogWarning(
                "Projection scope status list truncated. returned={Returned} total={Total} take={Take}",
                result.Items.Count,
                totalCount,
                boundedTake);
        }

        return result.Items.Select(Map).ToList();
    }

    private static ProjectionScopeStatusSnapshot Map(ProjectionScopeStatusDocument document)
    {
        var singleSourceVersionGap = document.SourceVersions.Count == 1
            ? document.SourceVersions[0].VersionGap
            : (long?)null;
        return new ProjectionScopeStatusSnapshot(
            ScopeActorId: document.ScopeActorId,
            Active: document.Active,
            ReceivedEnvelopeTotal: document.ReceivedEnvelopeTotal,
            AttemptedEnvelopeTotal: document.AttemptedEnvelopeTotal,
            SuccessfulMaterializationTotal: document.SuccessfulMaterializationTotal,
            FailedAttemptTotal: document.FailedAttemptTotal,
            RetryExhaustedTotal: document.RetryExhaustedTotal,
            RetryExhaustedFailureCount: document.RetryExhaustedFailureCount,
            UnresolvedFailureCount: document.UnresolvedFailureCount,
            OldestUnresolvedFailureAt: document.OldestUnresolvedFailureAtUtc?.ToDateTimeOffset(),
            FailureDiagnosticDroppedTotal: document.FailureDiagnosticDroppedTotal,
            SourceActorCount: document.SourceVersions.Count,
            SingleSourceVersionGap: singleSourceVersionGap,
            UpdatedAt: document.UpdatedAt);
    }
}
