namespace Aevatar.CQRS.Projection.Core.Abstractions;

// Read-model LIST companion to IProjectionScopeWatermarkQueryPort (which reads a single scope's
// watermark). This lists every materialized projection-scope status so an operations surface can
// render per-scope health (version lag = observed - successful, active flag, failure count).
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
// ScopeActorId is the scope/actor key; Observed/Successful are the last observed/successful versions;
// Lag is the derived backlog (never negative). UpdatedAt exposes the read-model's freshness watermark.
public sealed record ProjectionScopeStatusSnapshot(
    string ScopeActorId,
    bool Active,
    long LastObservedVersion,
    long LastSuccessfulVersion,
    int FailureCount,
    long Lag,
    DateTimeOffset UpdatedAt);
