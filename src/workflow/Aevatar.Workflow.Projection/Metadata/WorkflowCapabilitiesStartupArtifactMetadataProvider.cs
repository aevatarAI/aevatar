using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Metadata;

// Refactor (iter94/cluster-094b):
//   Old: workflow capabilities was a current-state document with fake StateVersion = 1 and LastEventId = startup-materialization.
//   New: workflow capabilities is a startup artifact with honest GeneratedAtUtc and SchemaVersion watermarks, without fake authoritative version fields.
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
