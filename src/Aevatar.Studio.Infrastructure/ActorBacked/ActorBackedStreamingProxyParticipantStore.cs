using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.StreamingProxyParticipant;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Read-only implementation of <see cref="IStreamingProxyParticipantStore"/>.
/// Reads from the projection document store (CQRS read model).
/// </summary>
internal sealed class ActorBackedStreamingProxyParticipantStore
    : IStreamingProxyParticipantStore
{
    private const string WriteActorId = "streaming-proxy-participants";

    private readonly IProjectionDocumentReader<StreamingProxyParticipantCurrentStateDocument, string> _documentReader;
    public ActorBackedStreamingProxyParticipantStore(
        IProjectionDocumentReader<StreamingProxyParticipantCurrentStateDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    // Refactor (iter43/issue-865-streaming-proxy-room-chat-host-orchestration):
    //   Old pattern: StreamingProxy chat endpoint and participant coordinator fetch runtime actor objects, run Nyx participant discussion loops, mutate participant side-store state, and dispatch room events from Host/Application-side orchestration.
    //   New principle: StreamingProxyGAgent owns participant admission, reply rounds, leave/failure decisions, and terminal-state publication; Host submits one typed command and observes projection/readmodel events only. Coordinator is adapter-only for Nyx external calls.
    public async Task<IReadOnlyList<StreamingProxyParticipant>> ListAsync(
        string roomId, CancellationToken cancellationToken = default)
    {
        var document = await _documentReader.GetAsync(WriteActorId, cancellationToken);
        if (document?.StateRoot == null ||
            !document.StateRoot.Is(StreamingProxyParticipantGAgentState.Descriptor))
            return [];

        var state = document.StateRoot.Unpack<StreamingProxyParticipantGAgentState>();
        if (!state.Rooms.TryGetValue(roomId, out var list))
            return [];

        return list.Participants
            .Select(p => new StreamingProxyParticipant(
                p.AgentId,
                p.DisplayName,
                p.JoinedAt.ToDateTimeOffset()))
            .ToList()
            .AsReadOnly();
    }
}
