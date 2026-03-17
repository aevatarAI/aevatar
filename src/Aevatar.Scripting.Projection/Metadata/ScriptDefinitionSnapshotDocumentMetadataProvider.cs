using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Metadata;

public sealed class ScriptDefinitionSnapshotDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<ScriptDefinitionSnapshotDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "script-definition-snapshots",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
