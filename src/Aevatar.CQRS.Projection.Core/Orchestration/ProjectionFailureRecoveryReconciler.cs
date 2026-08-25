using Aevatar.CQRS.Projection.Stores.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionFailureRecoveryReconciler
{
    internal const int PageSize = 200;
    internal const int MaxReplayItemsPerScope = 100;

    private readonly IProjectionDocumentReader<ProjectionScopeStatusDocument, string> _documentReader;
    private readonly IProjectionFailureReplayService _replayService;
    private readonly ILogger<ProjectionFailureRecoveryReconciler> _logger;

    public ProjectionFailureRecoveryReconciler(
        IProjectionDocumentReader<ProjectionScopeStatusDocument, string> documentReader,
        IProjectionFailureReplayService replayService,
        ILogger<ProjectionFailureRecoveryReconciler>? logger = null)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _replayService = replayService ?? throw new ArgumentNullException(nameof(replayService));
        _logger = logger ?? NullLogger<ProjectionFailureRecoveryReconciler>.Instance;
    }

    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        var candidateCount = 0;
        var dispatchedCount = 0;
        string? cursor = null;
        do
        {
            var result = await ReadPageAsync(cursor, ct).ConfigureAwait(false);
            candidateCount += result.Items.Count;
            foreach (var candidate in result.Items)
            {
                if (await TryDispatchAsync(candidate, ct).ConfigureAwait(false))
                    dispatchedCount++;
            }

            var nextCursor = result.NextCursor;
            if (!string.IsNullOrWhiteSpace(nextCursor) &&
                string.Equals(cursor, nextCursor, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Projection failure recovery query returned a non-advancing cursor. cursor={Cursor}",
                    cursor);
                break;
            }

            cursor = nextCursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        if (candidateCount > 0)
        {
            _logger.LogInformation(
                "Projection failure recovery sweep completed. candidateCount={CandidateCount} dispatchedCount={DispatchedCount}",
                candidateCount,
                dispatchedCount);
        }

        return dispatchedCount;
    }

    private async Task<bool> TryDispatchAsync(
        ProjectionScopeStatusDocument candidate,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var eligibleFailureCount = Math.Max(
            0,
            candidate.UnresolvedFailureCount - candidate.RetryExhaustedFailureCount);
        if (!candidate.Active || candidate.Released || eligibleFailureCount == 0)
            return false;

        if (string.IsNullOrWhiteSpace(candidate.RootActorId) ||
            string.IsNullOrWhiteSpace(candidate.ProjectionKind))
        {
            _logger.LogWarning(
                "Projection failure recovery skipped malformed scope status. actorId={ActorId}",
                candidate.ScopeActorId);
            return false;
        }

        var scopeKey = new ProjectionRuntimeScopeKey(
            candidate.RootActorId,
            candidate.ProjectionKind,
            ProjectionScopeModeMapper.ToRuntime(candidate.Mode),
            candidate.SessionId);
        var expectedActorId = ProjectionScopeActorId.Build(scopeKey);
        if (!string.Equals(expectedActorId, candidate.ScopeActorId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Projection failure recovery skipped inconsistent scope identity. actorId={ActorId} expectedActorId={ExpectedActorId}",
                candidate.ScopeActorId,
                expectedActorId);
            return false;
        }

        try
        {
            return await _replayService.ReplayAutomaticallyAsync(
                    scopeKey,
                    Math.Max(1, candidate.StateVersion),
                    Math.Min(MaxReplayItemsPerScope, eligibleFailureCount),
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Projection failure recovery dispatch failed. actorId={ActorId} unresolvedFailureCount={UnresolvedFailureCount}",
                candidate.ScopeActorId,
                candidate.UnresolvedFailureCount);
            return false;
        }
    }

    private Task<ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>> ReadPageAsync(
        string? cursor,
        CancellationToken ct) =>
        _documentReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Cursor = cursor,
                Take = PageSize,
                Filters =
                [
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(ProjectionScopeStatusDocument.Active),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromBool(true),
                    },
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(ProjectionScopeStatusDocument.Released),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromBool(false),
                    },
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(ProjectionScopeStatusDocument.UnresolvedFailureCount),
                        Operator = ProjectionDocumentFilterOperator.Gt,
                        Value = ProjectionDocumentValue.FromInt64(0),
                    },
                ],
                Sorts =
                [
                    new ProjectionDocumentSort
                    {
                        FieldPath = nameof(ProjectionScopeStatusDocument.RetryExhaustedFailureCount),
                        Direction = ProjectionDocumentSortDirection.Asc,
                    },
                    new ProjectionDocumentSort
                    {
                        FieldPath = nameof(ProjectionScopeStatusDocument.OldestUnresolvedFailureAtUtc),
                        Direction = ProjectionDocumentSortDirection.Asc,
                    },
                    new ProjectionDocumentSort
                    {
                        FieldPath = nameof(ProjectionScopeStatusDocument.Id),
                        Direction = ProjectionDocumentSortDirection.Asc,
                    },
                ],
            },
            ct);
}
