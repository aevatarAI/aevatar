namespace Aevatar.CQRS.Projection.Core.Abstractions;

// Read-model LIST companion to IProjectionScopeWatermarkQueryPort (which reads a single scope's
// watermark). This lists every materialized projection-scope status so an operations surface can
// render per-scope processing and unresolved-failure health without subtracting unrelated actor versions.
//
// Query path reads the materialized ProjectionScopeStatusDocument read-model ONLY: it never replays
// event streams, rebuilds state, or touches IEventStore (the same invariant as the single-scope port).
public interface IProjectionScopeStatusListQueryPort
{
    Task<IReadOnlyList<ProjectionScopeStatusSnapshot>> ListAsync(
        ProjectionScopeStatusListQuery query,
        CancellationToken ct = default);
}

// Read-side query parameters for listing projection-scope statuses. Take is bounded by the impl.
public sealed record ProjectionScopeStatusListQuery
{
    public int Take { get; init; } = 200;
}

// Read-side projection of a single ProjectionScopeStatusDocument, shaped for the operations surface.
// A version gap is exposed only when the scope contains exactly one authoritative source actor axis.
public sealed record ProjectionScopeStatusSnapshot(
    string ScopeActorId,
    bool Active,
    long ReceivedEnvelopeTotal,
    long AttemptedEnvelopeTotal,
    long SuccessfulMaterializationTotal,
    long FailedAttemptTotal,
    long RetryExhaustedTotal,
    int RetryExhaustedFailureCount,
    int UnresolvedFailureCount,
    DateTimeOffset? OldestUnresolvedFailureAt,
    long FailureDiagnosticDroppedTotal,
    int SourceActorCount,
    long? SingleSourceVersionGap,
    DateTimeOffset UpdatedAt);
