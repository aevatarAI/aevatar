using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StreamingProxyParticipant;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IStreamingProxyParticipantStore"/>.
/// Reads from the projection document store (CQRS read model).
/// Writes send commands to the Write GAgent.
/// </summary>
internal sealed class ActorBackedStreamingProxyParticipantStore
    : IStreamingProxyParticipantStore
{
    private const string WriteActorId = "streaming-proxy-participants";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IProjectionDocumentReader<StreamingProxyParticipantCurrentStateDocument, string> _documentReader;
    private readonly ILogger<ActorBackedStreamingProxyParticipantStore> _logger;

    public ActorBackedStreamingProxyParticipantStore(
        IStudioActorBootstrap bootstrap,
        IActorDispatchPort dispatchPort,
        IProjectionDocumentReader<StreamingProxyParticipantCurrentStateDocument, string> documentReader,
        ILogger<ActorBackedStreamingProxyParticipantStore> logger)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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

    public async Task AddAsync(
        string roomId, string agentId, string displayName,
        CancellationToken cancellationToken = default)
    {
        var actor = await EnsureWriteActorAsync(cancellationToken);
        var evt = new ParticipantAddedEvent
        {
            RoomId = roomId,
            AgentId = agentId,
            DisplayName = displayName,
            JoinedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        await ActorCommandDispatcher.SendAsync(_dispatchPort, actor, evt, cancellationToken);
    }

    public async Task RemoveParticipantAsync(
        string roomId, string agentId, CancellationToken cancellationToken = default)
    {
        var actor = await EnsureWriteActorAsync(cancellationToken);
        var evt = new ParticipantRemovedEvent
        {
            RoomId = roomId,
            AgentId = agentId,
        };
        await ActorCommandDispatcher.SendAsync(_dispatchPort, actor, evt, cancellationToken);
    }

    public async Task RemoveRoomAsync(
        string roomId, CancellationToken cancellationToken = default)
    {
        var actor = await EnsureWriteActorAsync(cancellationToken);
        var evt = new RoomParticipantsRemovedEvent
        {
            RoomId = roomId,
        };
        await ActorCommandDispatcher.SendAsync(_dispatchPort, actor, evt, cancellationToken);
    }

    // ── Actor resolution ──

    private Task<IActor> EnsureWriteActorAsync(CancellationToken ct) =>
        _bootstrap.EnsureAsync<StreamingProxyParticipantGAgent>(WriteActorId, ct);
}
