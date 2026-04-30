using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Metadata;

public sealed class ScriptCatalogEntryDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<ScriptCatalogEntryDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "script-catalog-entries",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
            ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["created_at_utc_value"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "date",
                },
                ["updated_at_utc_value"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "date",
                },
            },
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
