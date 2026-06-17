using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.Metadata;

public sealed class StudioWorkspaceCurrentStateDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<StudioWorkspaceCurrentStateDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "studio-workspaces",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = false,
            ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Keyword(),
                ["actor_id"] = Keyword(),
                ["state_version"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "long",
                },
                ["last_event_id"] = Keyword(),
                ["updated_at"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "date",
                },
                ["state_root_json"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "text",
                    ["index"] = false,
                },
            },
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));

    private static Dictionary<string, object?> Keyword() => new(StringComparer.Ordinal)
    {
        ["type"] = "keyword",
    };
}
