using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.Metadata;

public sealed class StudioTeamCurrentStateDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<StudioTeamCurrentStateDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "studio-teams",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
