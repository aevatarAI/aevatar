using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Metadata;

public sealed class WorkflowCapabilitiesStartupArtifactMetadataProvider
    : IProjectionDocumentMetadataProvider<WorkflowCapabilitiesStartupArtifact>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "workflow-capabilities-startup-artifacts",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
