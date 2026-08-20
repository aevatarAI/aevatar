using Aevatar.Workflow.Projection.ReadModels;
using static Aevatar.Workflow.Projection.Metadata.WorkflowDocumentMappingHelpers;

namespace Aevatar.Workflow.Projection.Metadata;

/// <summary>
/// Index metadata for the workflow actor current-state read model.
/// Every field the current-state query port and terminal-state reconciler filter, search or sort on
/// is mapped explicitly as keyword / date;
/// opaque payload text is stored but not indexed; never-queried plan / seed / approval subtrees are
/// disabled objects. Proto maps stay disabled objects through the descriptor augmenter.
/// </summary>
public sealed class WorkflowExecutionCurrentStateDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<WorkflowExecutionCurrentStateDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "workflow-execution-current-states",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
            ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // Filter / search / sort dimensions.
                ["root_actor_id"] = Keyword(),
                ["definition_actor_id"] = Keyword(),
                ["run_id"] = Keyword(),
                ["workflow_id"] = Keyword(),
                ["scope_id"] = Keyword(),
                ["schedule_id"] = Keyword(),
                ["status"] = Keyword(),
                ["saga_status"] = Keyword(),
                ["run_origin"] = Keyword(),
                ["workflow_name"] = SearchableKeyword(),
                ["input_summary"] = SearchableKeyword(),
                ["updated_at_utc_value"] = Date(),
                ["activity_initiator"] = ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["platform"] = Keyword(),
                    ["tenant"] = Keyword(),
                    ["external_user_id"] = Keyword(),
                    ["scope"] = Keyword(),
                    ["binding_id"] = Keyword(),
                    ["display_value"] = SearchableKeyword(),
                    ["availability"] = Keyword(),
                }),

                // Typed enums and identity-like fields the descriptor augmenter does not cover.
                ["expected_execution_mode"] = Keyword(),
                ["terminal_value_lifecycle_failure_kind"] = Keyword(),
                ["fork_seed_completed_step_id_entries"] = Keyword(),

                // Opaque payload text: stored, never indexed.
                ["workflow_yaml"] = NotIndexedText(),
                ["input"] = NotIndexedText(),
                ["final_output"] = NotIndexedText(),
                ["final_error"] = NotIndexedText(),
                ["compilation_error"] = NotIndexedText(),
                ["dead_letter_error"] = NotIndexedText(),
                ["activity_current_step"] = ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["step_id"] = Keyword(),
                    ["input_summary"] = NotIndexedText(),
                    ["availability"] = Keyword(),
                }),
                ["activity_first_failure"] = ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["step_id"] = Keyword(),
                    ["message"] = NotIndexedText(),
                    ["availability"] = Keyword(),
                }),
                ["activity_waiting"] = ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["step_id"] = Keyword(),
                    ["waiting_kind"] = Keyword(),
                    ["prompt"] = NotIndexedText(),
                    ["availability"] = Keyword(),
                }),
                ["recovery_capability"] = ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["retry_failed_step"] = RecoveryActionCapability(),
                    ["run_again"] = RecoveryActionCapability(),
                    ["workflow_definition_revision_id"] = Keyword(),
                }),
                ["lineage"] = ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["availability"] = Keyword(),
                    ["unavailable_reason"] = NotIndexedText(),
                    ["retry_fork"] = ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["availability"] = Keyword(),
                        ["source_run_id"] = Keyword(),
                        ["original_run_id"] = Keyword(),
                        ["start_at_step_id"] = Keyword(),
                        ["child_runs"] = LineageRunRef(),
                    }),
                    ["sub_workflow"] = ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["availability"] = Keyword(),
                        ["parent_run_id"] = Keyword(),
                        ["parent_actor_id"] = Keyword(),
                        ["parent_step_id"] = Keyword(),
                        ["root_run_id"] = Keyword(),
                        ["child_runs"] = LineageRunRef(),
                    }),
                }),

                // Never-queried plan / seed / approval subtrees: stored, not mapped.
                ["input_file_ref_entries"] = DisabledObject(),
                ["connector_approval_entries"] = DisabledObject(),
                ["capability_admission_plan"] = DisabledObject(),
            },
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));

    private static Dictionary<string, object?> RecoveryActionCapability() =>
        ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["eligibility"] = Keyword(),
            ["unavailable_reason_code"] = Keyword(),
            ["unavailable_reason"] = NotIndexedText(),
            ["recommended_actions"] = Keyword(),
            ["starting_step_id"] = Keyword(),
        });

    private static Dictionary<string, object?> LineageRunRef() =>
        ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["run_id"] = Keyword(),
            ["actor_id"] = Keyword(),
            ["relationship_id"] = Keyword(),
            ["step_id"] = Keyword(),
            ["relation_kind"] = Keyword(),
        });
}
