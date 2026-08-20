using Aevatar.Workflow.Projection.ReadModels;
using static Aevatar.Workflow.Projection.Metadata.WorkflowDocumentMappingHelpers;

namespace Aevatar.Workflow.Projection.Metadata;

/// <summary>
/// Index metadata for the workflow actor binding read model. The binding reader and the NyxID
/// admission startup guard filter on actor kind / run / scope / definition and sort by update
/// time and actor id; those dimensions are mapped explicitly. The workflow YAML is stored but
/// never indexed and the admission plan subtree is a disabled object.
/// </summary>
public sealed class WorkflowActorBindingDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<WorkflowActorBindingDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "workflow-actor-bindings",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
            ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["actor_id"] = Keyword(),
                ["actor_kind_value"] = Integer(),
                ["definition_actor_id"] = Keyword(),
                ["run_id"] = Keyword(),
                ["scope_id"] = Keyword(),
                ["workflow_id"] = Keyword(),
                ["workflow_name"] = SearchableKeyword(),
                ["source_kind"] = Keyword(),
                ["expected_execution_mode"] = Keyword(),
                ["updated_at_utc_value"] = Date(),
                ["workflow_yaml"] = NotIndexedText(),
                ["capability_admission_plan"] = DisabledObject(),
            },
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
