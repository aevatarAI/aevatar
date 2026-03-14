using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Metadata;

public sealed class ScriptReadModelDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<ScriptReadModelDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "script-read-model-documents",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
