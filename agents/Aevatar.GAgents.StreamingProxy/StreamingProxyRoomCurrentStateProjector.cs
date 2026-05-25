using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.StreamingProxy;

/// <summary>
/// Materializes <see cref="StreamingProxyGAgentState"/> committed events into
/// <see cref="StreamingProxyRoomCurrentStateDocument"/> in the projection document store.
/// </summary>
public sealed class StreamingProxyRoomCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StreamingProxyCurrentStateProjectionContext>
{
    private readonly IProjectionWriteDispatcher<StreamingProxyRoomCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public StreamingProxyRoomCurrentStateProjector(
        IProjectionWriteDispatcher<StreamingProxyRoomCurrentStateDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        StreamingProxyCurrentStateProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<StreamingProxyGAgentState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state == null)
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);

        var document = new StreamingProxyRoomCurrentStateDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            StateRoot = Any.Pack(state),
        };

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
