using Aevatar.Foundation.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.ReadModels;

public sealed class ScriptCatalogEntryDocument
    : AevatarReadModelBase,
      IProjectionReadModel,
      IProjectionReadModelCloneable<ScriptCatalogEntryDocument>
{
    public string CatalogActorId { get; set; } = string.Empty;
    public string ScriptId { get; set; } = string.Empty;
    public string ActiveRevision { get; set; } = string.Empty;
    public string ActiveDefinitionActorId { get; set; } = string.Empty;
    public string ActiveSourceHash { get; set; } = string.Empty;
    public string PreviousRevision { get; set; } = string.Empty;
    public List<string> RevisionHistory { get; set; } = [];
    public string LastProposalId { get; set; } = string.Empty;

    public ScriptCatalogEntryDocument DeepClone() =>
        new()
        {
            Id = Id,
            StateVersion = StateVersion,
            LastEventId = LastEventId,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            CatalogActorId = CatalogActorId,
            ScriptId = ScriptId,
            ActiveRevision = ActiveRevision,
            ActiveDefinitionActorId = ActiveDefinitionActorId,
            ActiveSourceHash = ActiveSourceHash,
            PreviousRevision = PreviousRevision,
            RevisionHistory = RevisionHistory.ToList(),
            LastProposalId = LastProposalId,
        };
}
