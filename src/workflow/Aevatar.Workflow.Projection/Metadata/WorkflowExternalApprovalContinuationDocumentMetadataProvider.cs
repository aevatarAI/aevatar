using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Metadata;

public sealed class WorkflowExternalApprovalContinuationDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<WorkflowExternalApprovalContinuationDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "workflow-external-approval-continuations",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
