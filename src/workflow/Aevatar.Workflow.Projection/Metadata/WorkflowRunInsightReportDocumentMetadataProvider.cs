using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Metadata;

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
                ["input"] = NotIndexedText(),
                ["final_output"] = NotIndexedText(),
                ["final_error"] = NotIndexedText(),
                ["step_entries"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "object",
                    ["dynamic"] = true,
                    ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["output_preview"] = NotIndexedText(),
                        ["error"] = NotIndexedText(),
                        ["assigned_value"] = NotIndexedText(),
                        ["suspension_prompt"] = NotIndexedText(),
                        ["suspension_content"] = NotIndexedText(),
                        ["failure_output"] = NotIndexedText(),
                        ["file_item_results"] = DisabledObject(),
                        ["vote_agreement_decision"] = DisabledObject(),
                        ["latest_failed_attempt"] = DisabledObject(),
                    },
                },
                ["role_reply_entries"] = ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["content"] = NotIndexedText(),
                }),
                ["timeline_entries"] = ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["message"] = NotIndexedText(),
                }),
                ["operation_entries"] = ObjectWithProperties(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
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

    private static Dictionary<string, object?> DisabledObject() => new(StringComparer.Ordinal)
    {
        ["type"] = "object",
        ["enabled"] = false,
    };

    private static Dictionary<string, object?> NotIndexedText() => new(StringComparer.Ordinal)
    {
        ["type"] = "text",
        ["index"] = false,
    };

    private static Dictionary<string, object?> ObjectWithProperties(
        IReadOnlyDictionary<string, object?> properties) => new(StringComparer.Ordinal)
    {
        ["type"] = "object",
        ["dynamic"] = true,
        ["properties"] = properties,
    };
}
