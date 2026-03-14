using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Metadata;

public sealed class ScriptEvolutionReadModelMetadataProvider
    : IProjectionDocumentMetadataProvider<ScriptEvolutionReadModel>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "script-evolution-read-models",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
