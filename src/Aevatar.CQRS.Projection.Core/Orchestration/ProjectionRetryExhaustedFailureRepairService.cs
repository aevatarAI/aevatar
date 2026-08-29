namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionRetryExhaustedFailureRepairService
    : IProjectionRetryExhaustedFailureRepairService
{
    private readonly IProjectionScopeIntrospectionQueryPort _introspection;
    private readonly IProjectionFailureReplayService _replayService;

    public ProjectionRetryExhaustedFailureRepairService(
        IProjectionScopeIntrospectionQueryPort introspection,
        IProjectionFailureReplayService replayService)
    {
        _introspection = introspection ?? throw new ArgumentNullException(nameof(introspection));
        _replayService = replayService ?? throw new ArgumentNullException(nameof(replayService));
    }

    public async Task<ProjectionRetryExhaustedFailureRepairResult> RepairAsync(
        ProjectionRetryExhaustedFailureRepairRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (!IsValidRequest(request))
            return Result(ProjectionRetryExhaustedFailureRepairStatus.InvalidRequest, request);

        var snapshot = await _introspection.GetAsync(request.ScopeActorId, ct).ConfigureAwait(false);
        if (snapshot == null)
            return Result(ProjectionRetryExhaustedFailureRepairStatus.ScopeNotFound, request);

        var scopeKey = new ProjectionRuntimeScopeKey(
            snapshot.RootActorId,
            snapshot.ProjectionKind,
            snapshot.Mode,
            snapshot.SessionId);
        string expectedScopeActorId;
        try
        {
            expectedScopeActorId = ProjectionScopeActorId.Build(scopeKey);
        }
        catch (ArgumentException)
        {
            return Result(
                ProjectionRetryExhaustedFailureRepairStatus.ScopeIdentityInvalid,
                request,
                snapshot);
        }

        if (!string.Equals(snapshot.ScopeActorId, request.ScopeActorId, StringComparison.Ordinal) ||
            !string.Equals(expectedScopeActorId, request.ScopeActorId, StringComparison.Ordinal))
        {
            return Result(
                ProjectionRetryExhaustedFailureRepairStatus.ScopeIdentityMismatch,
                request,
                snapshot);
        }

        if (!snapshot.Active || snapshot.Released)
        {
            return Result(
                ProjectionRetryExhaustedFailureRepairStatus.ScopeNotActive,
                request,
                snapshot);
        }

        if (snapshot.StateVersion != request.ExpectedScopeStateVersion ||
            snapshot.UnresolvedFailureCount != request.ExpectedUnresolvedFailureCount ||
            snapshot.RetryExhaustedFailureCount != request.ExpectedRetryExhaustedFailureCount)
        {
            return Result(
                ProjectionRetryExhaustedFailureRepairStatus.ManifestChanged,
                request,
                snapshot);
        }

        var dispatched = await _replayService.ReplayRetryExhaustedAsync(
                new ProjectionRetryExhaustedFailuresRequest(
                    scopeKey,
                    request.ExpectedScopeStateVersion,
                    request.ExpectedUnresolvedFailureCount,
                    request.ExpectedRetryExhaustedFailureCount,
                    request.MaxItems,
                    request.RequestId.Trim(),
                    request.Reason.Trim(),
                    request.RequestedBySubjectId.Trim()),
                ct)
            .ConfigureAwait(false);

        return Result(
            dispatched
                ? ProjectionRetryExhaustedFailureRepairStatus.AcceptedForDispatch
                : ProjectionRetryExhaustedFailureRepairStatus.RecoveryIdentityUnavailable,
            request,
            snapshot);
    }

    private static bool IsValidRequest(ProjectionRetryExhaustedFailureRepairRequest request) =>
        !string.IsNullOrWhiteSpace(request.ScopeActorId) &&
        request.ExpectedScopeStateVersion > 0 &&
        request.ExpectedUnresolvedFailureCount > 0 &&
        request.ExpectedRetryExhaustedFailureCount > 0 &&
        request.ExpectedRetryExhaustedFailureCount <= request.ExpectedUnresolvedFailureCount &&
        request.MaxItems is > 0 and <= ProjectionFailureRecoveryReconciler.MaxReplayItemsPerScope &&
        request.MaxItems <= request.ExpectedRetryExhaustedFailureCount &&
        IsValidRequestId(request.RequestId) &&
        IsBoundedOperatorText(request.Reason, 256) &&
        IsBoundedOperatorText(request.RequestedBySubjectId, 256);

    private static bool IsValidRequestId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 128 &&
               normalized.All(static character =>
                   char.IsAsciiLetterOrDigit(character) ||
                   character is '.' or '_' or ':' or '-');
    }

    private static bool IsBoundedOperatorText(string? value, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 &&
               normalized.Length <= maxLength &&
               normalized.All(static character =>
                   !char.IsControl(character) &&
                   character is not '\r' and not '\n' and not '\u2028' and not '\u2029');
    }

    private static ProjectionRetryExhaustedFailureRepairResult Result(
        ProjectionRetryExhaustedFailureRepairStatus status,
        ProjectionRetryExhaustedFailureRepairRequest request,
        ProjectionScopeIntrospectionSnapshot? snapshot = null) =>
        new(
            status,
            snapshot?.ScopeActorId ?? request.ScopeActorId,
            request.RequestId?.Trim() ?? string.Empty,
            snapshot?.StateVersion ?? 0,
            snapshot?.UnresolvedFailureCount ?? 0,
            snapshot?.RetryExhaustedFailureCount ?? 0,
            request.MaxItems);
}
