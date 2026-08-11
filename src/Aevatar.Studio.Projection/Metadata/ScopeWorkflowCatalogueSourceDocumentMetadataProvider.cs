using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.Metadata;

public sealed class ScopeWorkflowCatalogueSourceDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<ScopeWorkflowCatalogueSourceDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "scope-workflow-catalogue-sources",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = false,
            ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Keyword(),
                ["actor_id"] = Keyword(),
                ["state_version"] = Long(),
                ["last_event_id"] = Keyword(),
                ["updated_at"] = Date(),
                ["scope_id"] = Keyword(),
                ["workflow_id"] = Keyword(),
                ["source_kind"] = Keyword(),
                ["name"] = Keyword(),
                ["description"] = Keyword(index: false),
                ["source_updated_at_utc_value"] = Date(),
                ["service_key"] = Keyword(),
                ["workflow_name"] = Keyword(),
                ["committed_actor_id"] = Keyword(),
                ["active_revision_id"] = Keyword(),
                ["deployment_id"] = Keyword(),
                ["deployment_status"] = Keyword(),
                ["service_app_id"] = Keyword(),
                ["service_namespace"] = Keyword(),
                ["published_service_id"] = Keyword(),
                ["team_id"] = Keyword(),
                ["member_id"] = Keyword(),
                ["last_bound_revision_id"] = Keyword(),
            },
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));

    private static Dictionary<string, object?> Keyword(bool index = true) => new(StringComparer.Ordinal)
    {
        ["type"] = "keyword",
        ["index"] = index,
    };

    private static Dictionary<string, object?> Date() => new(StringComparer.Ordinal)
    {
        ["type"] = "date",
    };

    private static Dictionary<string, object?> Long() => new(StringComparer.Ordinal)
    {
        ["type"] = "long",
    };
}
