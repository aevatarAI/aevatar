using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Metadata;

public sealed class WorkflowActorBindingDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<WorkflowActorBindingDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "workflow-actor-bindings",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
