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
                ["step_entries"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "object",
                    ["dynamic"] = true,
                    ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["failure_output"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = "text",
                            ["index"] = false,
                        },
                        ["file_item_results"] = DisabledObject(),
                        ["vote_agreement_decision"] = DisabledObject(),
                        ["latest_failed_attempt"] = DisabledObject(),
                    },
                },
            },
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));

    private static Dictionary<string, object?> DisabledObject() => new(StringComparer.Ordinal)
    {
        ["type"] = "object",
        ["enabled"] = false,
    };
}
