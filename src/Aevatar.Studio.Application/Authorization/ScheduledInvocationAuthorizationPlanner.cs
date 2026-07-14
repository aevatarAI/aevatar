using System.Security.Cryptography;
using Google.Protobuf;

namespace Aevatar.Studio.Application.Authorization;

public sealed class ScheduledInvocationAuthorizationPlanner : IScheduledInvocationAuthorizationPlanner
{
    public const string PolicyVersion = "scheduled-invocation-auth/v1";
    private readonly INyxIdCatalogSnapshotQueryPort _snapshotQueryPort;

    public ScheduledInvocationAuthorizationPlanner(INyxIdCatalogSnapshotQueryPort snapshotQueryPort)
    {
        _snapshotQueryPort = snapshotQueryPort;
    }

    public async Task<ScheduledInvocationAuthorizationPlanResult> PlanAsync(
        ScheduledInvocationAuthorizationRequest request,
        CancellationToken ct = default)
    {
        var contextFailure = ValidateContext(request);
        if (contextFailure is not null)
            return contextFailure;

        var snapshot = await _snapshotQueryPort.GetAsync(request.Owner, ct);
        if (snapshot is null)
            return Failed(ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound, "nyxid_catalog_snapshot_not_found");
        if (!OwnerEquals(request.Owner, snapshot.Owner))
            return Failed(ScheduledInvocationAuthorizationFailureCode.OwnerMismatch, "nyxid_catalog_owner_mismatch");
        if (snapshot.FreshUntilUtc <= request.EvaluatedAtUtc)
            return Failed(ScheduledInvocationAuthorizationFailureCode.SnapshotStale, "nyxid_catalog_snapshot_stale");

        var services = snapshot.Services.ToDictionary(static service => service.UserServiceId, StringComparer.Ordinal);
        var requiredServiceIds = request.RequiredNyxIdServiceIds.ToList();
        foreach (var slug in request.RequiredNyxIdServiceSlugs
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .Select(static value => value.Trim())
                     .Distinct(StringComparer.Ordinal))
        {
            var matches = snapshot.Services
                .Where(service => string.Equals(service.ServiceSlug, slug, StringComparison.Ordinal))
                .Select(static service => service.UserServiceId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (matches.Length != 1)
                return Failed(ScheduledInvocationAuthorizationFailureCode.ServiceNotFound, $"nyxid_service_slug_not_unique:{slug}");
            requiredServiceIds.Add(matches[0]);
        }
        var selected = new List<NyxIdServiceGrant>();
        foreach (var serviceId in requiredServiceIds
                     .Where(static id => !string.IsNullOrWhiteSpace(id))
                     .Select(static id => id.Trim())
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            if (!services.TryGetValue(serviceId, out var service))
                return Failed(ScheduledInvocationAuthorizationFailureCode.ServiceNotFound, $"nyxid_service_not_found:{serviceId}");
            if (snapshot.UnreachableServiceIds?.Contains(serviceId) == true)
                return Failed(ScheduledInvocationAuthorizationFailureCode.OwnerMismatch, $"nyxid_service_unreachable:{serviceId}");
            if (!service.NodeGrantsNotRequired && service.NodeGrants.Count == 0)
                return Failed(ScheduledInvocationAuthorizationFailureCode.NodeGrantMissing, $"nyxid_node_grant_missing:{serviceId}");

            selected.Add(Normalize(service));
        }

        if (selected.Count == 0 && !request.ServiceGrantsNotRequired)
            return Failed(ScheduledInvocationAuthorizationFailureCode.ServiceNotFound, "nyxid_service_grants_empty");

        var plan = new ScheduledInvocationAuthorizationPlan
        {
            InvocationTarget = request.InvocationTarget.Clone(),
            Owner = request.Owner.Clone(),
            CredentialPolicy = new ScheduledInvocationCredentialPolicy
            {
                Scopes = "read proxy",
                AllowAllServices = false,
                AllowAllNodes = false,
                ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(request.ExpiresAtUtc),
                PolicyVersion = PolicyVersion,
                ServiceGrantsNotRequired = request.ServiceGrantsNotRequired,
            },
            Authority = request.Authority.Clone(),
            Disclosure = new ScheduledInvocationAuthorizationDisclosure
            {
                DedicatedToSchedule = true,
                SecretManagedByAevatar = true,
                BrowserReceivesRawKey = false,
                DeleteRevokesCredential = true,
                PauseResumeRevokesCredential = false,
            },
        };
        plan.Authority.CatalogStateVersion = snapshot.StateVersion;
        plan.Authority.CatalogObservedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(snapshot.ObservedAtUtc);
        plan.Authority.CatalogFreshUntil = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(snapshot.FreshUntilUtc);
        plan.Authority.CatalogExternalRevision = snapshot.ExternalRevision;
        plan.Authority.CatalogContentDigest = snapshot.ContentDigest;
        plan.NyxIdServiceGrants.Add(selected);
        plan.PermissionDigest = ComputeDigest(plan);
        return ScheduledInvocationAuthorizationPlanResult.Succeeded(plan);
    }

    private static ScheduledInvocationAuthorizationPlanResult? ValidateContext(
        ScheduledInvocationAuthorizationRequest request)
    {
        if (request.InvocationTarget.TargetCase == ScheduledInvocationTarget.TargetOneofCase.None)
            return Failed(ScheduledInvocationAuthorizationFailureCode.OwnerMismatch, "invocation_target_missing");
        if (request.OwnerContext.Owner == null ||
            string.IsNullOrWhiteSpace(request.Owner.Authority) ||
            request.Owner.OwnerKind == NyxIdCatalogOwnerKind.Unspecified ||
            string.IsNullOrWhiteSpace(request.Owner.OwnerSubject) ||
            string.IsNullOrWhiteSpace(request.OwnerContext.SubjectPlatform) ||
            string.IsNullOrWhiteSpace(request.OwnerContext.SubjectExternalUserId) ||
            string.IsNullOrWhiteSpace(request.OwnerContext.VerifiedBindingId))
        {
            return Failed(ScheduledInvocationAuthorizationFailureCode.OwnerMismatch, "authenticated_owner_context_incomplete");
        }

        return null;
    }

    public static string ComputeDigest(ScheduledInvocationAuthorizationPlan plan)
    {
        var canonical = plan.Clone();
        canonical.PermissionDigest = string.Empty;
        return Convert.ToHexStringLower(SHA256.HashData(canonical.ToByteArray()));
    }

    private static NyxIdServiceGrant Normalize(NyxIdServiceGrant service)
    {
        var normalized = new NyxIdServiceGrant
        {
            UserServiceId = service.UserServiceId.Trim(),
            DisplayName = service.DisplayName.Trim(),
            NodeGrantsNotRequired = service.NodeGrantsNotRequired,
            ServiceSlug = service.ServiceSlug.Trim(),
        };
        normalized.NodeGrants.Add(service.NodeGrants
            .OrderByDescending(static node => node.Primary)
            .ThenBy(static node => node.NodeId, StringComparer.Ordinal)
            .Select(static node => new NyxIdNodeGrant
            {
                NodeId = node.NodeId.Trim(),
                DisplayName = node.DisplayName.Trim(),
                Primary = node.Primary,
            }));
        return normalized;
    }

    private static bool OwnerEquals(NyxIdCatalogOwnerIdentity left, NyxIdCatalogOwnerIdentity right) =>
        string.Equals(left.Authority, right.Authority, StringComparison.Ordinal) &&
        left.OwnerKind == right.OwnerKind &&
        string.Equals(left.OwnerSubject, right.OwnerSubject, StringComparison.Ordinal);

    private static ScheduledInvocationAuthorizationPlanResult Failed(
        ScheduledInvocationAuthorizationFailureCode code,
        string detail) => ScheduledInvocationAuthorizationPlanResult.Failed(code, detail);
}
