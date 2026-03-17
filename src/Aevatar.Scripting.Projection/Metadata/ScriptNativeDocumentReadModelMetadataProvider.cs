using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Metadata;

public sealed class ScriptNativeDocumentReadModelMetadataProvider
    : IProjectionDocumentMetadataProvider<ScriptNativeDocumentReadModel>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "script-native-read-models",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
