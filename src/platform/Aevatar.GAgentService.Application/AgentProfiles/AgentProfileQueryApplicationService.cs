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
        var summary = entry?.PublishedSummary;
        if (entry is null ||
            entry.Status != AgentProfileProvisioningStatus.Active ||
            !entry.Reference.Equals(normalizedReference) ||
            summary is null ||
            !summary.Reference.Equals(normalizedReference) ||
            !IsValidDiscoveryEntry(entry, summary))
        {
            return null;
        }

        var execution = await _executionQuery.GetAsync(entry.ProfileId, ct);
        return new AgentProfileDiscoverySnapshot(
            summary.Reference,
            summary.DisplayName,
            summary.Purpose,
            summary.PublishedRevision,
            IsAvailable(entry, execution));
    }

    private static bool IsValidDiscoveryEntry(
        AgentProfileNamespaceEntrySnapshot entry,
        AgentProfilePublishedSummary summary)
    {
        var identity = new AgentProfileIdentity
        {
            ProfileId = entry.ProfileId,
            Owner = entry.Owner,
            OwningScopeId = entry.OwningScopeId,
            Reference = entry.Reference,
        };
        return AgentProfilePolicies.ValidateIdentity(identity).Count == 0 &&
            AgentProfilePolicies.ValidatePublishedSummary(summary).Count == 0;
    }

    private static bool IsAvailable(
        AgentProfileNamespaceEntrySnapshot entry,
        AgentProfileExecutionSnapshot? execution)
    {
        var summary = entry.PublishedSummary;
        var snapshot = execution?.Snapshot;
        var identity = snapshot?.Identity;
        if (summary is null ||
            execution is null ||
            !string.Equals(execution.ProfileId, entry.ProfileId, StringComparison.Ordinal) ||
            identity is null ||
            !string.Equals(identity.ProfileId, entry.ProfileId, StringComparison.Ordinal) ||
            identity.Owner is null ||
            !identity.Owner.Equals(entry.Owner) ||
            !string.Equals(identity.OwningScopeId, entry.OwningScopeId, StringComparison.Ordinal) ||
            identity.Reference is null ||
            !identity.Reference.Equals(entry.Reference) ||
            snapshot!.PublishedRevision != summary.PublishedRevision ||
            !snapshot.SnapshotSha256.Equals(summary.SnapshotSha256))
        {
            return false;
        }

        try
        {
            return snapshot.SnapshotSha256.Equals(
                AgentProfileDeterminism.ComputeExecutionSnapshotSha256(snapshot));
        }
        catch (AgentProfileContractValidationException)
        {
            return false;
        }
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
        if (!TryNormalizeCaller(caller, out var callerOwner, out var callerScopeId) ||
            !IsValidProfileSlug(profileSlug))
        {
            return null;
        }

        var entry = await _namespaceQuery.GetOwnedAsync(
            callerOwner,
            callerScopeId,
            profileSlug,
            ct);
        if (entry is null ||
            entry.Status != AgentProfileProvisioningStatus.Active ||
            !Owns(callerOwner, callerScopeId, entry) ||
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

    internal static bool IsValidCaller(AgentProfileCallerContext? caller) =>
        TryNormalizeCaller(caller, out _, out _);

    internal static bool TryNormalizeCaller(
        AgentProfileCallerContext? caller,
        out AgentProfileOwnerIdentity owner,
        out string scopeId)
    {
        owner = new AgentProfileOwnerIdentity();
        scopeId = string.Empty;
        if (caller is null)
            return false;

        try
        {
            var identity = AgentProfileDeterminism.NormalizeIdentity(new AgentProfileIdentity
            {
                ProfileId = "caller",
                Owner = new AgentProfileOwnerIdentity { User = caller.Owner },
                OwningScopeId = caller.ScopeId,
                Reference = new AgentProfileReference
                {
                    OwnerHandle = "caller",
                    ProfileSlug = "caller",
                },
            });
            owner = identity.Owner;
            scopeId = identity.OwningScopeId;
            return true;
        }
        catch (AgentProfileContractValidationException)
        {
            return false;
        }
    }

    private static bool IsValidProfileSlug(string? profileSlug) =>
        AgentProfilePolicies.ValidateReference(new AgentProfileReference
        {
            OwnerHandle = "owner",
            ProfileSlug = profileSlug ?? string.Empty,
        }).Count == 0;

    private static bool Owns(
        AgentProfileOwnerIdentity callerOwner,
        string callerScopeId,
        AgentProfileNamespaceEntrySnapshot entry) =>
        entry.Owner.OwnerCase == AgentProfileOwnerIdentity.OwnerOneofCase.User &&
        entry.Owner.Equals(callerOwner) &&
        string.Equals(entry.OwningScopeId, callerScopeId, StringComparison.Ordinal);

    private static bool Matches(
        AgentProfileNamespaceEntrySnapshot entry,
        AgentProfileManagementSnapshot management)
    {
        var identity = management.Identity;
        if (AgentProfilePolicies.ValidateIdentity(identity).Count > 0 ||
            !string.Equals(management.ProfileId, entry.ProfileId, StringComparison.Ordinal) ||
            identity.Owner is null ||
            !identity.Owner.Equals(entry.Owner) ||
            !string.Equals(identity.OwningScopeId, entry.OwningScopeId, StringComparison.Ordinal) ||
            identity.Reference is null ||
            !identity.Reference.Equals(entry.Reference))
        {
            return false;
        }

        try
        {
            return management.DraftSha256.Equals(
                AgentProfileDeterminism.ComputeDraftSha256(management.Draft));
        }
        catch (AgentProfileContractValidationException)
        {
            return false;
        }
    }
}
