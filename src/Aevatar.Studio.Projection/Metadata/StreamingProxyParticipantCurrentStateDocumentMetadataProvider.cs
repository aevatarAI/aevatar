using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.Metadata;

public sealed class StreamingProxyParticipantCurrentStateDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<StreamingProxyParticipantCurrentStateDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "studio-streaming-proxy-participant",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
