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
/// Writes go through <see cref="StreamingProxyParticipantGAgent"/> event handlers.
/// Reads come from a readmodel snapshot maintained via event subscription.
/// </summary>
internal sealed class ActorBackedStreamingProxyParticipantStore
    : IStreamingProxyParticipantStore, IAsyncDisposable
{
    private const string ParticipantActorId = "streaming-proxy-participants";

    private readonly IActorRuntime _runtime;
    private readonly IActorEventSubscriptionProvider _subscriptions;
    private readonly ILogger<ActorBackedStreamingProxyParticipantStore> _logger;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile StreamingProxyParticipantGAgentState? _snapshot;
    private IAsyncDisposable? _subscription;
    private bool _initialized;

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
        await EnsureInitializedAsync(cancellationToken);

        var state = _snapshot;
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
        var actor = await EnsureActorAsync(cancellationToken);
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
        var actor = await EnsureActorAsync(cancellationToken);
        var evt = new RoomParticipantsRemovedEvent
        {
            RoomId = roomId,
        };
        await SendCommandAsync(actor, evt, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
            return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized)
                return;

            // Subscribe to the participant actor's events to receive state snapshots
            _subscription = await _subscriptions.SubscribeAsync<EventEnvelope>(
                ParticipantActorId,
                HandleParticipantEventAsync,
                ct);

            // Activate the actor — this triggers event replay + OnActivateAsync
            // which publishes the initial state snapshot
            await EnsureActorAsync(ct);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private Task HandleParticipantEventAsync(EventEnvelope envelope)
    {
        if (envelope.Payload is null)
            return Task.CompletedTask;

        if (envelope.Payload.Is(StreamingProxyParticipantStateSnapshotEvent.Descriptor))
        {
            var snapshot = envelope.Payload.Unpack<StreamingProxyParticipantStateSnapshotEvent>();
            _snapshot = snapshot.Snapshot;
            _logger.LogDebug(
                "Participant readmodel updated: {RoomCount} rooms",
                snapshot.Snapshot?.Rooms.Count ?? 0);
        }

        return Task.CompletedTask;
    }

    private async Task<IActor> EnsureActorAsync(CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(ParticipantActorId);
        if (actor is not null)
            return actor;

        return await _runtime.CreateAsync<StreamingProxyParticipantGAgent>(ParticipantActorId, ct);
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
