using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class AgentProfileCatalogQueryReader : IAgentProfileCatalogQueryPort
{
    private readonly IProjectionDocumentReader<AgentProfileCatalogReadModel, string> _reader;

    public AgentProfileCatalogQueryReader(
        IProjectionDocumentReader<AgentProfileCatalogReadModel, string> reader) =>
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public async Task<AgentProfileCatalogSnapshot?> GetAsync(
        AgentProfileOwner owner,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var actorId = AgentProfileActorIds.Namespace(owner);
        var document = await _reader.GetAsync(actorId, ct);
        return document is null || document.Owner is null ||
               !AgentProfileDeterminism.SameOwner(document.Owner, owner) ||
               !string.Equals(document.ActorId, actorId, StringComparison.Ordinal)
            ? null
            : new AgentProfileCatalogSnapshot(
            document.ActorId,
            document.StateVersion,
            document.Owner.Clone(),
            document.Profiles.Select(static x => x.Clone()).ToArray(),
            document.DefaultBindings.Select(static x => x.Clone()).ToArray(),
            document.LastMutation?.Clone(),
            document.UpdatedAt);
    }
}

public sealed class AgentProfileManagementQueryReader : IAgentProfileManagementQueryPort
{
    private readonly IProjectionDocumentReader<AgentProfileManagementReadModel, string> _reader;

    public AgentProfileManagementQueryReader(
        IProjectionDocumentReader<AgentProfileManagementReadModel, string> reader) =>
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public async Task<AgentProfileManagementSnapshot?> GetAsync(
        AgentProfileIdentity identity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!IsValidIdentity(identity))
            return null;
        var actorId = AgentProfileActorIds.Profile(identity.ProfileId);
        var document = await _reader.GetAsync(actorId, ct);
        return document is null || document.Identity is null ||
               !document.Identity.Equals(identity) ||
               !string.Equals(document.ActorId, actorId, StringComparison.Ordinal)
            ? null
            : new AgentProfileManagementSnapshot(
            document.ActorId,
            document.StateVersion,
            document.Identity.Clone(),
            document.Draft?.Clone(),
            document.DraftRevision,
            document.DraftSha256,
            document.PublishedDisplayName,
            document.PublishedPurpose,
            document.PublishedRevision,
            document.PublishedSnapshotSha256,
            document.PublishedAt?.ToDateTimeOffset(),
            document.LastMutation?.Clone(),
            document.UpdatedAt);
    }

    private static bool IsValidIdentity(AgentProfileIdentity identity) =>
        identity.Owner is not null &&
        identity.Owner.OwnerCase != AgentProfileOwner.OwnerOneofCase.None &&
        !string.IsNullOrWhiteSpace(identity.ProfileId) &&
        AgentProfilePolicies.ValidateProfileSlug(identity.ProfileSlug).Count == 0;
}

public sealed class AgentProfileExecutionQueryReader : IAgentProfileExecutionQueryPort
{
    private readonly IProjectionDocumentReader<AgentProfileExecutionReadModel, string> _reader;

    public AgentProfileExecutionQueryReader(
        IProjectionDocumentReader<AgentProfileExecutionReadModel, string> reader) =>
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public async Task<AgentProfileExecutionSnapshot?> GetAsync(
        AgentProfileBindingTarget target,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!IsValidTarget(target))
            return null;
        var actorId = AgentProfileActorIds.Profile(target.ProfileId);
        var document = await _reader.GetAsync(actorId, ct);
        return document?.Identity is null || document.Snapshot is null ||
               !string.Equals(document.ActorId, actorId, StringComparison.Ordinal) ||
               !AgentProfileDeterminism.SameOwner(document.Identity.Owner, target.Owner) ||
               !string.Equals(document.Identity.ProfileId, target.ProfileId, StringComparison.Ordinal) ||
               document.Snapshot.PublishedRevision != target.PublishedRevision ||
               !document.Snapshot.SnapshotSha256.Equals(target.SnapshotSha256)
            ? null
            : new AgentProfileExecutionSnapshot(
            document.ActorId,
            document.StateVersion,
            document.Identity.Clone(),
            document.Snapshot.Clone(),
            document.UpdatedAt);
    }

    private static bool IsValidTarget(AgentProfileBindingTarget target) =>
        target.Owner is not null &&
        target.Owner.OwnerCase != AgentProfileOwner.OwnerOneofCase.None &&
        !string.IsNullOrWhiteSpace(target.ProfileId) &&
        target.PublishedRevision > 0 &&
        target.SnapshotSha256.Length == 32;
}
