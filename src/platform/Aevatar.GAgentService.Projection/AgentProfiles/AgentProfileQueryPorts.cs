using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Core.AgentProfiles;

namespace Aevatar.GAgentService.Projection.AgentProfiles;

public sealed class ProjectionAgentProfileNamespaceQueryPort : IAgentProfileNamespaceQueryPort
{
    private readonly IProjectionDocumentReader<AgentProfileNamespaceCatalogDocument, string> _reader;

    public ProjectionAgentProfileNamespaceQueryPort(
        IProjectionDocumentReader<AgentProfileNamespaceCatalogDocument, string> reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<AgentProfileNamespaceEntrySnapshot?> GetOwnedAsync(
        AgentProfileOwnerIdentity owner,
        string owningScopeId,
        string profileSlug,
        CancellationToken ct = default)
    {
        if (owner == null ||
            AgentProfilePolicies.ValidateOwnerIdentity(owner).Count > 0 ||
            string.IsNullOrWhiteSpace(owningScopeId) ||
            string.IsNullOrWhiteSpace(profileSlug))
        {
            return null;
        }

        var document = await _reader.GetAsync(AgentProfileActorIds.Namespace, ct);
        var entry = document?.Entries.FirstOrDefault(candidate =>
            candidate.Status == AgentProfileProvisioningStatus.Active &&
            candidate.Owner != null &&
            candidate.Owner.Equals(owner) &&
            string.Equals(candidate.OwningScopeId, owningScopeId, StringComparison.Ordinal) &&
            candidate.Reference != null &&
            string.Equals(candidate.Reference.ProfileSlug, profileSlug, StringComparison.Ordinal));
        return document == null || entry == null ? null : Map(document, entry);
    }

    public async Task<AgentProfileNamespaceEntrySnapshot?> GetByReferenceAsync(
        AgentProfileReference reference,
        CancellationToken ct = default)
    {
        if (reference == null || AgentProfilePolicies.ValidateReference(reference).Count > 0)
            return null;

        var document = await _reader.GetAsync(AgentProfileActorIds.Namespace, ct);
        var entry = document?.Entries.FirstOrDefault(candidate =>
            candidate.Status == AgentProfileProvisioningStatus.Active &&
            candidate.Reference != null &&
            candidate.Reference.Equals(reference));
        return document == null || entry == null ? null : Map(document, entry);
    }

    private static AgentProfileNamespaceEntrySnapshot Map(
        AgentProfileNamespaceCatalogDocument document,
        AgentProfileCatalogEntryDocument entry) =>
        new(
            document.StateVersion,
            document.LastEventId,
            entry.ProfileId,
            entry.Reference.Clone(),
            entry.Owner.Clone(),
            entry.OwningScopeId,
            entry.Status,
            entry.PublishedSummary?.Clone());
}

public sealed class ProjectionAgentProfileManagementQueryPort : IAgentProfileManagementQueryPort
{
    private readonly IProjectionDocumentReader<AgentProfileOwnerDocument, string> _reader;

    public ProjectionAgentProfileManagementQueryPort(
        IProjectionDocumentReader<AgentProfileOwnerDocument, string> reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<AgentProfileManagementSnapshot?> GetAsync(
        string profileId,
        CancellationToken ct = default)
    {
        if (!IsCanonicalProfileId(profileId))
            return null;

        var document = await _reader.GetAsync(profileId, ct);
        return document == null
            ? null
            : new AgentProfileManagementSnapshot(
                document.StateVersion,
                document.LastEventId,
                document.Identity.Clone(),
                document.Draft.Clone(),
                document.DraftRevision,
                document.DraftSha256,
                document.PublishedRevision,
                document.PublishedSnapshotSha256,
                document.PublishedSourceDraftSha256,
                document.LastMutation?.Clone());
    }

    private static bool IsCanonicalProfileId(string? profileId) =>
        !string.IsNullOrWhiteSpace(profileId) &&
        string.Equals(profileId, profileId.Trim(), StringComparison.Ordinal);
}

public sealed class ProjectionAgentProfileExecutionSnapshotQueryPort
    : IAgentProfileExecutionSnapshotQueryPort
{
    private readonly IProjectionDocumentReader<AgentProfileExecutionDocument, string> _reader;

    public ProjectionAgentProfileExecutionSnapshotQueryPort(
        IProjectionDocumentReader<AgentProfileExecutionDocument, string> reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<AgentProfileExecutionSnapshot?> GetAsync(
        string profileId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileId) ||
            !string.Equals(profileId, profileId.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        var document = await _reader.GetAsync(profileId, ct);
        return document == null
            ? null
            : new AgentProfileExecutionSnapshot(
                document.StateVersion,
                document.LastEventId,
                document.Snapshot.Clone());
    }
}
