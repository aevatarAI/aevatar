using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.StreamingProxy;

public sealed class StreamingProxyRoomCurrentStateDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<StreamingProxyRoomCurrentStateDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "studio-streaming-proxy-room",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
