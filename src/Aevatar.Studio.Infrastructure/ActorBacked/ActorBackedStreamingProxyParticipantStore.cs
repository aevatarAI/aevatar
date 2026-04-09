using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.StreamingProxyParticipant;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
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
        await SendCommandAsync(actor, evt, cancellationToken);
    }

    public async Task RemoveRoomAsync(
        string roomId, CancellationToken cancellationToken = default)
    {
        var actor = await EnsureWriteActorAsync(cancellationToken);
        var evt = new RoomParticipantsRemovedEvent
        {
            RoomId = roomId,
        };
        await SendCommandAsync(actor, evt, cancellationToken);
    }

    // ── Per-request readmodel read (no service-level state) ──

    private async Task<StreamingProxyParticipantGAgentState?> ReadFromReadModelAsync(CancellationToken ct)
    {
        var readModelActorId = WriteActorId + "-readmodel";
        var tcs = new TaskCompletionSource<StreamingProxyParticipantGAgentState?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await _subscriptions.SubscribeAsync<EventEnvelope>(
            readModelActorId,
            envelope =>
            {
                if (envelope.Payload?.Is(StreamingProxyParticipantStateSnapshotEvent.Descriptor) == true)
                {
                    var snapshot = envelope.Payload.Unpack<StreamingProxyParticipantStateSnapshotEvent>();
                    tcs.TrySetResult(snapshot.Snapshot);
                }
                return Task.CompletedTask;
            },
            ct);

        // Activate readmodel actor (triggers OnActivateAsync → PublishAsync snapshot)
        await EnsureReadModelActorAsync(readModelActorId, ct);

        // Wait for snapshot with timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Timeout waiting for readmodel snapshot from {ActorId}", readModelActorId);
            return null;
        }
    }

    // ── Actor resolution ──

    private async Task<IActor> EnsureWriteActorAsync(CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(WriteActorId);
        return actor ?? await _runtime.CreateAsync<StreamingProxyParticipantGAgent>(WriteActorId, ct);
    }

    private async Task EnsureReadModelActorAsync(string readModelActorId, CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(readModelActorId);
        if (actor is null)
            await _runtime.CreateAsync<StreamingProxyParticipantReadModelGAgent>(readModelActorId, ct);
    }

    private static async Task SendCommandAsync(IActor actor, IMessage command, CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };
        await actor.HandleEventAsync(envelope, ct);
    }
}
