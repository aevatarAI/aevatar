using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.StreamingProxyParticipant;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IStreamingProxyParticipantStore"/>.
/// Completely stateless: no fields hold snapshot or subscription state.
/// Reads use per-request temporary subscription to the ReadModel GAgent.
/// Writes send commands to the Write GAgent.
/// </summary>
internal sealed class ActorBackedStreamingProxyParticipantStore
    : IStreamingProxyParticipantStore
{
    private const string WriteActorId = "streaming-proxy-participants";

    private readonly IActorRuntime _runtime;
    private readonly IActorEventSubscriptionProvider _subscriptions;
    private readonly ILogger<ActorBackedStreamingProxyParticipantStore> _logger;

    public ActorBackedStreamingProxyParticipantStore(
        IActorRuntime runtime,
        IActorEventSubscriptionProvider subscriptions,
        ILogger<ActorBackedStreamingProxyParticipantStore> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<StreamingProxyParticipant>> ListAsync(
        string roomId, CancellationToken cancellationToken = default)
    {
        var state = await ReadFromReadModelAsync(cancellationToken);
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

    // ── Per-request readmodel read (no service-level state) ──

    private Task<StreamingProxyParticipantGAgentState?> ReadFromReadModelAsync(CancellationToken ct)
    {
        return ReadModelSnapshotReader.ReadAsync<StreamingProxyParticipantGAgentState, StreamingProxyParticipantStateSnapshotEvent>(
            _subscriptions,
            _runtime,
            WriteActorId + "-readmodel",
            typeof(StreamingProxyParticipantReadModelGAgent),
            StreamingProxyParticipantStateSnapshotEvent.Descriptor,
            evt => evt.Snapshot,
            _logger,
            ct);
    }

    // ── Actor resolution ──

    private async Task<IActor> EnsureWriteActorAsync(CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(WriteActorId);
        return actor ?? await _runtime.CreateAsync<StreamingProxyParticipantGAgent>(WriteActorId, ct);
    }
}
