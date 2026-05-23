using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.StreamingProxy;

// Refactor (iter43/issue-865-streaming-proxy-room-chat-host-orchestration):
//   Old pattern: StreamingProxy chat endpoint and participant coordinator fetch runtime actor objects, run Nyx participant discussion loops, mutate participant side-store state, and dispatch room events from Host/Application-side orchestration.
//   New principle: StreamingProxyGAgent owns participant admission, reply rounds, leave/failure decisions, and terminal-state publication; Host submits one typed command and observes projection/readmodel events only. Coordinator is adapter-only for Nyx external calls.
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
