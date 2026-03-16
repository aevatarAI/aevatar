using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Metadata;

public sealed class WorkflowRunGraphArtifactDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<WorkflowRunGraphArtifactDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "workflow-run-graph-artifacts",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
