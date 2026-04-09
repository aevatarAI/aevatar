using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StreamingProxyParticipant;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IStreamingProxyParticipantStore"/>.
/// Reads the write actor's state directly.
/// Writes send commands to the Write GAgent.
/// </summary>
internal sealed class ActorBackedStreamingProxyParticipantStore
    : IStreamingProxyParticipantStore
{
    private const string WriteActorId = "streaming-proxy-participants";

    private readonly IActorRuntime _runtime;
    private readonly ILogger<ActorBackedStreamingProxyParticipantStore> _logger;

    public ActorBackedStreamingProxyParticipantStore(
        IActorRuntime runtime,
        ILogger<ActorBackedStreamingProxyParticipantStore> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<StreamingProxyParticipant>> ListAsync(
        string roomId, CancellationToken cancellationToken = default)
    {
        var state = await ReadWriteActorStateAsync(cancellationToken);
        if (state is null)
            return [];

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
        await ActorCommandDispatcher.SendAsync(actor, evt, cancellationToken);
    }

    public async Task RemoveRoomAsync(
        string roomId, CancellationToken cancellationToken = default)
    {
        var actor = await EnsureWriteActorAsync(cancellationToken);
        var evt = new RoomParticipantsRemovedEvent
        {
            RoomId = roomId,
        };
        await ActorCommandDispatcher.SendAsync(actor, evt, cancellationToken);
    }

    // ── Read write actor state directly ──

    private async Task<StreamingProxyParticipantGAgentState?> ReadWriteActorStateAsync(CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(WriteActorId);
        return (actor?.Agent as IAgent<StreamingProxyParticipantGAgentState>)?.State;
    }

    // ── Actor resolution ──

    private async Task<IActor> EnsureWriteActorAsync(CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(WriteActorId);
        return actor ?? await _runtime.CreateAsync<StreamingProxyParticipantGAgent>(WriteActorId, ct);
    }
}
