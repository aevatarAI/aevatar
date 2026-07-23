using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.GAgentService.Application.AgentProfiles;

public sealed class AgentProfileQueryApplicationService : IAgentProfileQueryService
{
    private readonly IAgentProfileNamespaceQueryPort _namespaceQuery;
    private readonly IAgentProfileExecutionSnapshotQueryPort _executionQuery;
    private readonly AgentProfileOwnerSnapshotResolver _ownerResolver;

    public AgentProfileQueryApplicationService(
        IAgentProfileNamespaceQueryPort namespaceQuery,
        IAgentProfileManagementQueryPort managementQuery,
        IAgentProfileExecutionSnapshotQueryPort executionQuery)
    {
        _namespaceQuery = namespaceQuery ?? throw new ArgumentNullException(nameof(namespaceQuery));
        ArgumentNullException.ThrowIfNull(managementQuery);
        _executionQuery = executionQuery ?? throw new ArgumentNullException(nameof(executionQuery));
        _ownerResolver = new AgentProfileOwnerSnapshotResolver(namespaceQuery, managementQuery);
    }

    public async Task<AgentProfileManagementSnapshot?> GetOwnedAsync(
        AgentProfileCallerContext caller,
        string profileSlug,
        CancellationToken ct = default)
    {
        var owned = await _ownerResolver.ResolveAsync(caller, profileSlug, ct);
        return owned?.Management.DeepClone();
    }

    public async Task<AgentProfileDiscoverySnapshot?> ResolveVisibleAsync(
        AgentProfileCallerContext caller,
        AgentProfileReference reference,
        CancellationToken ct = default)
    {
        if (!AgentProfileOwnerSnapshotResolver.IsValidCaller(caller) ||
            reference is null ||
            AgentProfilePolicies.ValidateReference(reference).Count > 0)
        {
            return null;
        }

        var normalizedReference = AgentProfileDeterminism.NormalizeReference(reference);
        var entry = await _namespaceQuery.GetByReferenceAsync(normalizedReference, ct);
        if (entry is null ||
            entry.Status != AgentProfileProvisioningStatus.Active ||
            !entry.Reference.Equals(normalizedReference) ||
            entry.PublishedSummary is null ||
            !entry.PublishedSummary.Reference.Equals(normalizedReference))
        {
            return null;
        }

        var execution = await _executionQuery.GetAsync(entry.ProfileId, ct);
        return new AgentProfileDiscoverySnapshot(
            entry.PublishedSummary.Reference,
            entry.PublishedSummary.DisplayName,
            entry.PublishedSummary.Purpose,
            entry.PublishedSummary.PublishedRevision,
            IsAvailable(entry, execution));
    }

    private static bool IsAvailable(
        AgentProfileNamespaceEntrySnapshot entry,
        AgentProfileExecutionSnapshot? execution)
    {
        var summary = entry.PublishedSummary;
        var snapshot = execution?.Snapshot;
        var identity = snapshot?.Identity;
        return summary is not null &&
            execution is not null &&
            string.Equals(execution.ProfileId, entry.ProfileId, StringComparison.Ordinal) &&
            identity is not null &&
            string.Equals(identity.ProfileId, entry.ProfileId, StringComparison.Ordinal) &&
            identity.Owner is not null &&
            identity.Owner.Equals(entry.Owner) &&
            string.Equals(identity.OwningScopeId, entry.OwningScopeId, StringComparison.Ordinal) &&
            identity.Reference is not null &&
            identity.Reference.Equals(entry.Reference) &&
            snapshot!.PublishedRevision == summary.PublishedRevision &&
            snapshot.SnapshotSha256.Equals(summary.SnapshotSha256);
    }
}

internal sealed record AgentProfileOwnedSnapshot(
    AgentProfileNamespaceEntrySnapshot NamespaceEntry,
    AgentProfileManagementSnapshot Management);

internal sealed class AgentProfileOwnerSnapshotResolver
{
    private readonly IAgentProfileNamespaceQueryPort _namespaceQuery;
    private readonly IAgentProfileManagementQueryPort _managementQuery;

    public AgentProfileOwnerSnapshotResolver(
        IAgentProfileNamespaceQueryPort namespaceQuery,
        IAgentProfileManagementQueryPort managementQuery)
    {
        _namespaceQuery = namespaceQuery ?? throw new ArgumentNullException(nameof(namespaceQuery));
        _managementQuery = managementQuery ?? throw new ArgumentNullException(nameof(managementQuery));
    }

    public async Task<AgentProfileOwnedSnapshot?> ResolveAsync(
        AgentProfileCallerContext caller,
        string profileSlug,
        CancellationToken ct)
    {
        if (!IsValidCaller(caller) || !IsValidProfileSlug(profileSlug))
            return null;

        var callerOwner = new AgentProfileOwnerIdentity { User = caller.Owner };
        var entry = await _namespaceQuery.GetOwnedAsync(
            callerOwner,
            caller.ScopeId,
            profileSlug,
            ct);
        if (entry is null ||
            entry.Status != AgentProfileProvisioningStatus.Active ||
            !Owns(caller, entry) ||
            entry.Reference is null ||
            AgentProfilePolicies.ValidateUserReference(entry.Reference).Count > 0 ||
            !string.Equals(entry.Reference.ProfileSlug, profileSlug, StringComparison.Ordinal))
        {
            return null;
        }

        var management = await _managementQuery.GetAsync(entry.ProfileId, ct);
        if (management is null || !Matches(entry, management))
            return null;

        return new AgentProfileOwnedSnapshot(entry.DeepClone(), management.DeepClone());
    }

    internal static bool IsValidCaller(AgentProfileCallerContext? caller)
    {
        if (caller is null ||
            string.IsNullOrWhiteSpace(caller.ScopeId) ||
            !string.Equals(caller.ScopeId, caller.ScopeId.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        return AgentProfilePolicies.ValidateOwnerIdentity(new AgentProfileOwnerIdentity
        {
            User = caller.Owner,
        }).Count == 0;
    }

    private static bool IsValidProfileSlug(string? profileSlug) =>
        AgentProfilePolicies.ValidateReference(new AgentProfileReference
        {
            OwnerHandle = "owner",
            ProfileSlug = profileSlug ?? string.Empty,
        }).Count == 0;

    private static bool Owns(
        AgentProfileCallerContext caller,
        AgentProfileNamespaceEntrySnapshot entry) =>
        entry.Owner.OwnerCase == AgentProfileOwnerIdentity.OwnerOneofCase.User &&
        entry.Owner.User.Equals(caller.Owner) &&
        string.Equals(entry.OwningScopeId, caller.ScopeId, StringComparison.Ordinal);

    private static bool Matches(
        AgentProfileNamespaceEntrySnapshot entry,
        AgentProfileManagementSnapshot management)
    {
        var identity = management.Identity;
        return AgentProfilePolicies.ValidateIdentity(identity).Count == 0 &&
            string.Equals(management.ProfileId, entry.ProfileId, StringComparison.Ordinal) &&
            identity.Owner is not null &&
            identity.Owner.Equals(entry.Owner) &&
            string.Equals(identity.OwningScopeId, entry.OwningScopeId, StringComparison.Ordinal) &&
            identity.Reference is not null &&
            identity.Reference.Equals(entry.Reference);
    }
}
