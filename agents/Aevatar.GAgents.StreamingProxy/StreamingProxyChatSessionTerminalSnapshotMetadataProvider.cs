using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.StreamingProxy;

public sealed class StreamingProxyChatSessionTerminalSnapshotMetadataProvider
    : IProjectionDocumentMetadataProvider<StreamingProxyChatSessionTerminalSnapshot>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "streaming-proxy-chat-session-terminal",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
