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
        var document = await _reader.GetAsync(AgentProfileActorIds.Namespace(owner), ct);
        return document is null ? null : new AgentProfileCatalogSnapshot(
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
        string profileActorId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileActorId))
            return null;
        var document = await _reader.GetAsync(profileActorId.Trim(), ct);
        return document is null ? null : new AgentProfileManagementSnapshot(
            document.ActorId,
            document.StateVersion,
            document.Identity.Clone(),
            document.Draft?.Clone(),
            document.DraftRevision,
            document.DraftSha256,
            document.Published?.Clone(),
            document.PublishedRevision,
            document.LastMutation?.Clone(),
            document.UpdatedAt);
    }
}

public sealed class AgentProfileExecutionQueryReader : IAgentProfileExecutionQueryPort
{
    private readonly IProjectionDocumentReader<AgentProfileExecutionReadModel, string> _reader;

    public AgentProfileExecutionQueryReader(
        IProjectionDocumentReader<AgentProfileExecutionReadModel, string> reader) =>
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public async Task<AgentProfileExecutionSnapshot?> GetAsync(
        string profileActorId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileActorId))
            return null;
        var document = await _reader.GetAsync(profileActorId.Trim(), ct);
        return document?.Snapshot is null ? null : new AgentProfileExecutionSnapshot(
            document.ActorId,
            document.StateVersion,
            document.Identity.Clone(),
            document.Snapshot.Clone(),
            document.UpdatedAt);
    }
}
