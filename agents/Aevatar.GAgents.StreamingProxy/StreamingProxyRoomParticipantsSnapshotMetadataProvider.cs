using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.StreamingProxy;

public sealed class StreamingProxyRoomParticipantsSnapshotMetadataProvider
    : IProjectionDocumentMetadataProvider<StreamingProxyRoomParticipantsSnapshot>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "streaming-proxy-room-participants",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
