using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Metadata;

public sealed class WorkflowExecutionReportDocumentMetadataProvider
    : IReadModelDocumentMetadataProvider<WorkflowExecutionReport>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "workflow-execution-reports",
        MappingJson: "{}",
        Settings: new Dictionary<string, string>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, string>(StringComparer.Ordinal));
}
