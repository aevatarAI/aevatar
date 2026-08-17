using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
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
        CancellationToken ct = default) =>
        await ResolveCoreAsync(owner, requiredStateVersion, null, ct).ConfigureAwait(false);

    public async Task<NyxIdAuthorizationCatalogVisibilityResult> ResolveRequiredServicesAsync(
        AuthorizationOwnerIdentity owner,
        long requiredStateVersion,
        IReadOnlyList<NyxIdUserServiceCapabilityRef> requiredServices,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requiredServices);
        var requiredServiceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requiredService in requiredServices)
        {
            if (requiredService == null || string.IsNullOrWhiteSpace(requiredService.UserServiceId))
            {
                return Result(
                    NyxIdAuthorizationCatalogVisibilityStatus.Invalid,
                    requiredStateVersion,
                    0,
                    "nyxid_catalog_required_service_missing");
            }

            requiredServiceIds.Add(requiredService.UserServiceId.Trim());
        }

        return await ResolveCoreAsync(
                owner,
                requiredStateVersion,
                requiredServiceIds.Count == 0 ? null : requiredServiceIds,
                ct)
            .ConfigureAwait(false);
    }

    private async Task<NyxIdAuthorizationCatalogVisibilityResult> ResolveCoreAsync(
        AuthorizationOwnerIdentity owner,
        long requiredStateVersion,
        IReadOnlySet<string>? requiredServiceIds,
        CancellationToken ct)
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
        if (requiredServiceIds is { Count: > 0 })
        {
            foreach (var requiredServiceId in requiredServiceIds)
            {
                var matches = snapshot.Services
                    .Where(service => string.Equals(
                        service.UserServiceId,
                        requiredServiceId,
                        StringComparison.Ordinal))
                    .Take(2)
                    .ToArray();
                if (matches.Length == 0)
                {
                    return Result(
                        NyxIdAuthorizationCatalogVisibilityStatus.Invalid,
                        requiredStateVersion,
                        visibleStateVersion,
                        "nyxid_catalog_required_service_missing");
                }

                if (matches.Length > 1)
                {
                    return Result(
                        NyxIdAuthorizationCatalogVisibilityStatus.Invalid,
                        requiredStateVersion,
                        visibleStateVersion,
                        "nyxid_catalog_snapshot_invalid");
                }

                var authorityWindow = NyxIdAuthorizationCatalogIntegrity.EvaluateServiceAuthorityWindow(
                    snapshot,
                    matches[0],
                    now);
                if (!authorityWindow.Ready)
                {
                    LogRejectedRequiredServiceAuthorityWindow(
                        requiredStateVersion,
                        visibleStateVersion,
                        matches[0],
                        authorityWindow,
                        now);
                    return Result(
                        NyxIdAuthorizationCatalogVisibilityStatus.Stale,
                        requiredStateVersion,
                        visibleStateVersion,
                        "nyxid_catalog_snapshot_stale");
                }
            }
        }
        else if (snapshot.ObservedAtUtc > now || snapshot.FreshUntilUtc <= now)
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

    private void LogRejectedRequiredServiceAuthorityWindow(
        long requiredStateVersion,
        long visibleStateVersion,
        NyxIdAuthorizationServiceEvidence service,
        NyxIdAuthorizationServiceAuthorityWindowResult authorityWindow,
        DateTimeOffset now)
    {
        _logger.LogWarning(
            "NyxID required-service authority window was rejected. requiredStateVersion={RequiredStateVersion} visibleStateVersion={VisibleStateVersion} requiredUserServiceId={RequiredUserServiceId} authorityWindowStatus={AuthorityWindowStatus} nowUtc={NowUtc} serviceObservedAtUtc={ServiceObservedAtUtc} serviceFreshUntilUtc={ServiceFreshUntilUtc} serviceEvaluatedAtUtc={ServiceEvaluatedAtUtc} hasObservedAt={HasObservedAt} hasFreshUntil={HasFreshUntil} hasEvaluatedAt={HasEvaluatedAt} hasContractVersion={HasContractVersion} hasPolicyVersion={HasPolicyVersion}",
            requiredStateVersion,
            visibleStateVersion,
            service.UserServiceId,
            authorityWindow.Status,
            now,
            authorityWindow.ObservedAtUtc,
            authorityWindow.FreshUntilUtc,
            authorityWindow.ProviderEvaluatedAtUtc,
            service.ObservedAt != null,
            service.FreshUntil != null,
            service.EvaluatedAt != null,
            !string.IsNullOrWhiteSpace(service.AuthorityContractVersion),
            !string.IsNullOrWhiteSpace(service.AuthorityPolicyVersion));
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
