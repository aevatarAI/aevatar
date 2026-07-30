namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public sealed record AgentProfileCatalogSnapshot(
    string ActorId,
    long AuthorityStateVersion,
    AgentProfileOwner Owner,
    IReadOnlyList<AgentProfileCatalogEntry> Profiles,
    IReadOnlyList<AgentProfileDefaultBinding> DefaultBindings,
    AgentProfileMutationOutcome? LastMutation,
    DateTimeOffset UpdatedAt);

public sealed record AgentProfileManagementSnapshot(
    string ActorId,
    long AuthorityStateVersion,
    AgentProfileIdentity Identity,
    AgentProfileDraft? Draft,
    long DraftRevision,
    Google.Protobuf.ByteString DraftSha256,
    string PublishedDisplayName,
    string PublishedPurpose,
    long PublishedRevision,
    Google.Protobuf.ByteString PublishedSnapshotSha256,
    DateTimeOffset? PublishedAt,
    AgentProfileMutationOutcome? LastMutation,
    DateTimeOffset UpdatedAt);

public sealed record AgentProfileExecutionSnapshot(
    string ActorId,
    long AuthorityStateVersion,
    AgentProfileIdentity Identity,
    AgentProfilePublishedSnapshot Snapshot,
    DateTimeOffset UpdatedAt);

public interface IAgentProfileCatalogQueryPort
{
    Task<AgentProfileCatalogSnapshot?> GetAsync(
        AgentProfileOwner owner,
        CancellationToken ct = default);
}

public interface IAgentProfileManagementQueryPort
{
    Task<AgentProfileManagementSnapshot?> GetAsync(
        AgentProfileIdentity identity,
        CancellationToken ct = default);
}

public interface IAgentProfileExecutionQueryPort
{
    Task<AgentProfileExecutionSnapshot?> GetAsync(
        AgentProfileBindingTarget target,
        CancellationToken ct = default);
}
