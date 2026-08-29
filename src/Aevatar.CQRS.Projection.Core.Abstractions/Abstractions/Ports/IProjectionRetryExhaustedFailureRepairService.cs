namespace Aevatar.CQRS.Projection.Core.Abstractions;

public interface IProjectionRetryExhaustedFailureRepairService
{
    Task<ProjectionRetryExhaustedFailureRepairResult> RepairAsync(
        ProjectionRetryExhaustedFailureRepairRequest request,
        CancellationToken ct = default);
}

public sealed record ProjectionRetryExhaustedFailureRepairRequest(
    string ScopeActorId,
    long ExpectedScopeStateVersion,
    int ExpectedUnresolvedFailureCount,
    int ExpectedRetryExhaustedFailureCount,
    int MaxItems,
    string RequestId,
    string Reason,
    string RequestedBySubjectId);

public sealed record ProjectionRetryExhaustedFailureRepairResult(
    ProjectionRetryExhaustedFailureRepairStatus Status,
    string ScopeActorId,
    string RequestId,
    long CurrentScopeStateVersion,
    int CurrentUnresolvedFailureCount,
    int CurrentRetryExhaustedFailureCount,
    int MaxItems);

public enum ProjectionRetryExhaustedFailureRepairStatus
{
    Unspecified = 0,
    AcceptedForDispatch = 1,
    InvalidRequest = 2,
    ScopeNotFound = 3,
    ScopeNotActive = 4,
    ScopeIdentityInvalid = 5,
    ScopeIdentityMismatch = 6,
    ManifestChanged = 7,
    RecoveryIdentityUnavailable = 8,
}
