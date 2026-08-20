using Aevatar.Workflow.Projection.ReadModels;
using static Aevatar.Workflow.Projection.Metadata.WorkflowDocumentMappingHelpers;

namespace Aevatar.Workflow.Projection.Metadata;

/// <summary>
/// Index metadata for the workflow run insight report artifact. The report is read by key only,
/// so the explicit mappings exist to keep the index small and the schema deliberate: ids, enums,
/// names and timestamps are keyword / date; payload text (inputs, outputs, errors, prompts,
/// tool arguments) is stored but never indexed; bulky nested attempt / vote / file material and
/// proto maps (request parameters, completion annotations, timeline data) are disabled objects.
/// </summary>
public sealed class WorkflowRunInsightReportDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<WorkflowRunInsightReportDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "workflow-execution-reports",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
            ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["report_version"] = Keyword(),
                ["workflow_name"] = SearchableKeyword(),
                ["input"] = NotIndexedText(),
                ["final_output"] = NotIndexedText(),
                ["final_error"] = NotIndexedText(),
                ["usage_value"] = UsageMetrics(),
                ["topology_entries"] = ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["parent"] = Keyword(),
                    ["child"] = Keyword(),
                }),
                ["step_entries"] = ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["target_role"] = Keyword(),
                    ["assigned_variable"] = Keyword(),
                    ["requested_variable_name"] = Keyword(),
                    ["display_name"] = SearchableKeyword(),
                    ["outcome"] = Keyword(),
                    ["failure_outcome"] = Keyword(),
                    ["recovery_failure_kind"] = Keyword(),
                    ["retry_disposition"] = Keyword(),
                    ["usage_value"] = UsageMetrics(),
                    ["tool_approval_value"] = ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["execution_id"] = Keyword(),
                        ["tool_name"] = Keyword(),
                        ["tool_call_id"] = Keyword(),
                        ["approval_request_id"] = Keyword(),
                    }),
                    ["output_preview"] = NotIndexedText(),
                    ["error"] = NotIndexedText(),
                    ["assigned_value"] = NotIndexedText(),
                    ["suspension_prompt"] = NotIndexedText(),
                    ["suspension_content"] = NotIndexedText(),
                    ["failure_output"] = NotIndexedText(),
                    ["file_item_results"] = DisabledObject(),
                    ["vote_agreement_decision"] = DisabledObject(),
                    ["latest_failed_attempt"] = DisabledObject(),
                }),
                ["role_reply_entries"] = ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["timestamp_utc_value"] = Date(),
                    ["role_id"] = Keyword(),
                    ["session_id"] = Keyword(),
                    ["content"] = NotIndexedText(),
                }),
                ["timeline_entries"] = ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["stage"] = Keyword(),
                    ["message"] = NotIndexedText(),
                }),
                ["operation_entries"] = ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["session_id"] = Keyword(),
                    ["operation_id"] = Keyword(),
                    ["kind"] = Keyword(),
                    ["started_at_utc_value"] = Date(),
                    ["completed_at_utc_value"] = Date(),
                    ["role_actor_id"] = Keyword(),
                    ["model"] = Keyword(),
                    ["provider"] = Keyword(),
                    ["available_tool_names"] = Keyword(),
                    ["finish_reason"] = Keyword(),
                    ["usage_value"] = UsageMetrics(),
                    ["tool_call_id"] = Keyword(),
                    ["tool_name"] = Keyword(),
                    ["input_summary"] = NotIndexedText(),
                    ["output"] = NotIndexedText(),
                    ["error"] = NotIndexedText(),
                    ["arguments_json"] = NotIndexedText(),
                    ["result_json"] = NotIndexedText(),
                    ["reasoning_content"] = NotIndexedText(),
                }),
            },
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));

    private static Dictionary<string, object?> UsageMetrics() =>
        ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = Keyword(),
        });
}
