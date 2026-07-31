using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.NyxId;

internal sealed class NyxIdDurableAuthorizationCatalogInspector(
    INyxIdAuthorizationCatalogQueryPort? catalogQueryPort,
    TimeProvider timeProvider,
    ILogger logger)
{
    public async Task<ExternalCapabilitySourceStamp?> InspectAsync(
        ExternalWorkflowCapabilityAccessContext access,
        string userServiceId,
        string serviceSlugSnapshot,
        CancellationToken cancellationToken)
    {
        if (catalogQueryPort is null || string.IsNullOrWhiteSpace(access.CallerId))
            return null;

        var owner = new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = access.CallerId,
        };
        NyxIdAuthorizationCatalogSnapshot? snapshot;
        try
        {
            snapshot = await catalogQueryPort.GetAsync(owner, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "NyxID durable authorization catalog query failed. ownerAuthority={OwnerAuthority}, ownerKind={OwnerKind}, failureType={FailureType}",
                owner.Authority,
                owner.OwnerKind,
                exception.GetType().Name);
            return null;
        }

        if (!IsUsableCatalog(snapshot, owner))
            return null;

        var matches = snapshot!.Services
            .Where(service => string.Equals(service.UserServiceId, userServiceId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length != 1 ||
            !IsUsableGrant(matches[0], userServiceId, serviceSlugSnapshot))
        {
            return null;
        }

        return new ExternalCapabilitySourceStamp
        {
            SourceKind = ExternalCapabilitySourceKind.DurableAuthorizationCatalog,
            SourceId = NyxIdAuthorizationCatalogActorIds.Build(owner),
            SourceVersion = snapshot.StateVersion,
            ObservedAt = Timestamp.FromDateTimeOffset(snapshot.ObservedAtUtc),
            FreshUntil = Timestamp.FromDateTimeOffset(snapshot.FreshUntilUtc),
            ContentDigest = snapshot.ContentDigest,
        };
    }

    private bool IsUsableCatalog(
        NyxIdAuthorizationCatalogSnapshot? snapshot,
        AuthorizationOwnerIdentity expectedOwner)
    {
        if (snapshot is null ||
            snapshot.StateVersion <= 0 ||
            !snapshot.Activated ||
            snapshot.Invalidated ||
            snapshot.Cleaned ||
            !OwnerEquals(snapshot.Owner, expectedOwner) ||
            snapshot.ObservedAtUtc == default ||
            string.IsNullOrWhiteSpace(snapshot.ContractVersion) ||
            string.IsNullOrWhiteSpace(snapshot.PolicyVersion) ||
            snapshot.EvaluatedAtUtc == default ||
            string.IsNullOrWhiteSpace(snapshot.ContentDigest) ||
            !string.Equals(
                snapshot.ContentDigest,
                NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(snapshot.Owner, snapshot.Services),
                StringComparison.Ordinal))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        return snapshot.ObservedAtUtc <= now && snapshot.FreshUntilUtc > now;
    }

    private static bool IsUsableGrant(
        NyxIdAuthorizationServiceEvidence service,
        string userServiceId,
        string serviceSlugSnapshot)
    {
        if (string.IsNullOrWhiteSpace(service.UserServiceId) ||
            !string.Equals(service.UserServiceId, service.UserServiceId.Trim(), StringComparison.Ordinal) ||
            !string.Equals(service.UserServiceId, userServiceId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(service.ServiceSlug) ||
            !string.Equals(service.ServiceSlug, service.ServiceSlug.Trim(), StringComparison.Ordinal) ||
            !string.Equals(service.ServiceSlug, serviceSlugSnapshot, StringComparison.Ordinal) ||
            service.Access != NyxIdAuthorizationAccess.Permitted ||
            service.NodeGrantRequirement is not (
                AuthorizationGrantRequirement.Required or AuthorizationGrantRequirement.NotRequired) ||
            !IsNormalizedOwner(service.ResourceOwner))
        {
            return false;
        }

        string? previousNodeId = null;
        foreach (var nodeId in service.NodeIds)
        {
            if (string.IsNullOrWhiteSpace(nodeId) ||
                !string.Equals(nodeId, nodeId.Trim(), StringComparison.Ordinal) ||
                previousNodeId is not null && string.CompareOrdinal(previousNodeId, nodeId) >= 0)
            {
                return false;
            }
            previousNodeId = nodeId;
        }

        return service.NodeGrantRequirement == AuthorizationGrantRequirement.Required
            ? service.NodeIds.Count > 0
            : service.NodeIds.Count == 0;
    }

    private static bool IsNormalizedOwner(AuthorizationOwnerIdentity? owner) =>
        owner is not null &&
        string.Equals(owner.Authority, NyxIdAuthorizationAuthorities.NyxId, StringComparison.Ordinal) &&
        owner.OwnerKind != AuthorizationOwnerKind.Unspecified &&
        System.Enum.IsDefined(owner.OwnerKind) &&
        !string.IsNullOrWhiteSpace(owner.OwnerSubject) &&
        string.Equals(owner.OwnerSubject, owner.OwnerSubject.Trim(), StringComparison.Ordinal);

    private static bool OwnerEquals(
        AuthorizationOwnerIdentity? left,
        AuthorizationOwnerIdentity right) =>
        left is not null &&
        string.Equals(left.Authority, right.Authority, StringComparison.Ordinal) &&
        left.OwnerKind == right.OwnerKind &&
        string.Equals(left.OwnerSubject, right.OwnerSubject, StringComparison.Ordinal);
}
