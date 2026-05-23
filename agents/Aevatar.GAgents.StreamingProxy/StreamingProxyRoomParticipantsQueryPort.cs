using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.StreamingProxy;

// Refactor (iter43/issue-865-streaming-proxy-room-chat-host-orchestration):
//   Old pattern: StreamingProxy chat endpoint and participant coordinator fetch runtime actor objects, run Nyx participant discussion loops, mutate participant side-store state, and dispatch room events from Host/Application-side orchestration.
//   New principle: StreamingProxyGAgent owns participant admission, reply rounds, leave/failure decisions, and terminal-state publication; Host submits one typed command and observes projection/readmodel events only. Coordinator is adapter-only for Nyx external calls.
public interface IStreamingProxyRoomParticipantsQueryPort
{
    Task<StreamingProxyRoomParticipantsSnapshot?> GetAsync(
        string rootActorId,
        CancellationToken ct = default);
}

public sealed class StreamingProxyRoomParticipantsQueryPort
    : IStreamingProxyRoomParticipantsQueryPort
{
    private readonly IProjectionDocumentReader<StreamingProxyRoomParticipantsSnapshot, string> _documentReader;

    public StreamingProxyRoomParticipantsQueryPort(
        IProjectionDocumentReader<StreamingProxyRoomParticipantsSnapshot, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    // Refactor (iter43/issue-865-streaming-proxy-room-chat-host-orchestration):
    //   Old pattern: StreamingProxy chat endpoint and participant coordinator fetch runtime actor objects, run Nyx participant discussion loops, mutate participant side-store state, and dispatch room events from Host/Application-side orchestration.
    //   New principle: StreamingProxyGAgent owns participant admission, reply rounds, leave/failure decisions, and terminal-state publication; Host submits one typed command and observes projection/readmodel events only. Coordinator is adapter-only for Nyx external calls.
    public Task<StreamingProxyRoomParticipantsSnapshot?> GetAsync(
        string rootActorId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootActorId))
            return Task.FromResult<StreamingProxyRoomParticipantsSnapshot?>(null);

        return _documentReader.GetAsync(
            StreamingProxyRoomParticipantsProjector.ComposeSnapshotId(rootActorId),
            ct);
    }
}
