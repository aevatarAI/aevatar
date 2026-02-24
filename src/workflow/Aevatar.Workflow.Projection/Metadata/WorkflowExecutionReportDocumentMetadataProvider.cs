using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Metadata;

public sealed class WorkflowExecutionReportDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<WorkflowExecutionReport>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "workflow-execution-reports",
        MappingJson: "{}",
        Settings: new Dictionary<string, string>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, string>(StringComparer.Ordinal));
}
