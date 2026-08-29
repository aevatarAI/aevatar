namespace Aevatar.CQRS.Projection.Core.Abstractions;

public interface IProjectionFailureReplayService
{
    Task<bool> ReplayRetryExhaustedAsync(
        ProjectionRetryExhaustedFailuresRequest request,
        CancellationToken ct = default);

    Task<bool> ReplayAutomaticallyAsync(
        ProjectionRuntimeScopeKey scopeKey,
        long observedScopeStateVersion,
        int maxItems = 100,
        CancellationToken ct = default);
}

public sealed record ProjectionRetryExhaustedFailuresRequest(
    ProjectionRuntimeScopeKey ScopeKey,
    long ExpectedScopeStateVersion,
    int ExpectedUnresolvedFailureCount,
    int ExpectedRetryExhaustedFailureCount,
    int MaxItems,
    string RequestId,
    string Reason,
    string RequestedBySubjectId);
