using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.StreamingProxy;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;
using AppStreamingProxyParticipant = Aevatar.Studio.Application.Studio.Abstractions.StreamingProxyParticipant;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Projection-backed participant query port for StreamingProxy rooms.
/// </summary>
internal sealed class ProjectionStreamingProxyParticipantQueryPort
    : IStreamingProxyParticipantQueryPort
{
    private readonly IProjectionDocumentReader<StreamingProxyRoomCurrentStateDocument, string> _documentReader;
    private readonly ILogger<ProjectionStreamingProxyParticipantQueryPort> _logger;

    public ProjectionStreamingProxyParticipantQueryPort(
        IProjectionDocumentReader<StreamingProxyRoomCurrentStateDocument, string> documentReader,
        ILogger<ProjectionStreamingProxyParticipantQueryPort> logger)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<AppStreamingProxyParticipant>> ListAsync(
        string roomId, CancellationToken cancellationToken = default)
    {
        var document = await _documentReader.GetAsync(roomId, cancellationToken);
        if (document?.StateRoot == null ||
            !document.StateRoot.Is(StreamingProxyGAgentState.Descriptor))
        {
            _logger.LogDebug(
                "StreamingProxy room current-state document is not available for room {RoomId}.",
                roomId);
            return [];
        }

        var state = document.StateRoot.Unpack<StreamingProxyGAgentState>();

        return state.Participants
            .Select(p => new AppStreamingProxyParticipant(
                p.AgentId,
                p.DisplayName,
                p.JoinedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue))
            .ToList()
            .AsReadOnly();
    }
}
