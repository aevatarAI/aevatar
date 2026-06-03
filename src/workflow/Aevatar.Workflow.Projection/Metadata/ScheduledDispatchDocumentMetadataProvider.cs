using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Metadata;

public sealed class ScheduledDispatchDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<ScheduledDispatchDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "scheduled-dispatches",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
