using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Application.Schedules.Authorization;

public sealed class NyxIdAuthorizationCatalogVisibilityService
    : INyxIdAuthorizationCatalogVisibilityPort
{
    private readonly INyxIdAuthorizationCatalogQueryPort _catalogQueryPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NyxIdAuthorizationCatalogVisibilityService> _logger;

    public NyxIdAuthorizationCatalogVisibilityService(
        INyxIdAuthorizationCatalogQueryPort catalogQueryPort,
        TimeProvider timeProvider,
        ILogger<NyxIdAuthorizationCatalogVisibilityService>? logger = null)
    {
        _catalogQueryPort = catalogQueryPort ?? throw new ArgumentNullException(nameof(catalogQueryPort));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<NyxIdAuthorizationCatalogVisibilityService>.Instance;
    }

    public async Task<NyxIdAuthorizationCatalogVisibilityResult> ResolveAsync(
        AuthorizationOwnerIdentity owner,
        long requiredStateVersion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (requiredStateVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredStateVersion));

        NyxIdAuthorizationCatalogSnapshot? snapshot;
        try
        {
            snapshot = await _catalogQueryPort.GetAsync(owner, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to read NyxID authorization catalog visibility for owner kind {OwnerKind}.",
                owner.OwnerKind);
            return NyxIdAuthorizationCatalogVisibilityResult.Unavailable(requiredStateVersion);
        }

        var visibleStateVersion = snapshot?.StateVersion ?? 0;
        if (visibleStateVersion < requiredStateVersion)
        {
            return Result(
                NyxIdAuthorizationCatalogVisibilityStatus.ProjectionPending,
                requiredStateVersion,
                visibleStateVersion,
                "nyxid_catalog_projection_pending");
        }

        if (snapshot == null)
            return NyxIdAuthorizationCatalogVisibilityResult.Unavailable(requiredStateVersion);
        if (!OwnerEquals(owner, snapshot.Owner))
        {
            return Result(
                NyxIdAuthorizationCatalogVisibilityStatus.OwnerMismatch,
                requiredStateVersion,
                visibleStateVersion,
                "nyxid_catalog_owner_mismatch");
        }
        if (snapshot.Invalidated || snapshot.Cleaned)
        {
            return Result(
                NyxIdAuthorizationCatalogVisibilityStatus.Invalidated,
                requiredStateVersion,
                visibleStateVersion,
                "nyxid_catalog_snapshot_invalidated");
        }
        if (!snapshot.Activated ||
            snapshot.ObservedAtUtc == default ||
            string.IsNullOrWhiteSpace(snapshot.ContractVersion) ||
            string.IsNullOrWhiteSpace(snapshot.PolicyVersion) ||
            snapshot.EvaluatedAtUtc == default ||
            string.IsNullOrWhiteSpace(snapshot.ContentDigest))
        {
            return Result(
                NyxIdAuthorizationCatalogVisibilityStatus.Invalid,
                requiredStateVersion,
                visibleStateVersion,
                "nyxid_catalog_snapshot_invalid");
        }

        var now = _timeProvider.GetUtcNow();
        if (snapshot.ObservedAtUtc > now || snapshot.FreshUntilUtc <= now)
        {
            return Result(
                NyxIdAuthorizationCatalogVisibilityStatus.Stale,
                requiredStateVersion,
                visibleStateVersion,
                "nyxid_catalog_snapshot_stale");
        }

        return Result(
            NyxIdAuthorizationCatalogVisibilityStatus.Ready,
            requiredStateVersion,
            visibleStateVersion,
            string.Empty);
    }

    private static NyxIdAuthorizationCatalogVisibilityResult Result(
        NyxIdAuthorizationCatalogVisibilityStatus status,
        long requiredStateVersion,
        long visibleStateVersion,
        string failureCode) =>
        new(status, requiredStateVersion, visibleStateVersion, failureCode);

    private static bool OwnerEquals(AuthorizationOwnerIdentity left, AuthorizationOwnerIdentity right) =>
        string.Equals(left.Authority, right.Authority, StringComparison.Ordinal) &&
        left.OwnerKind == right.OwnerKind &&
        string.Equals(left.OwnerSubject, right.OwnerSubject, StringComparison.Ordinal);
}
