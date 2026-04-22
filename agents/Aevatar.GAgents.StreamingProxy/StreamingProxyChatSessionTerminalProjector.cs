using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.StreamingProxy;

public sealed class StreamingProxyChatSessionTerminalProjector
    : ICurrentStateProjectionMaterializer<StreamingProxyCurrentStateProjectionContext>
{
    private readonly IProjectionWriteDispatcher<StreamingProxyChatSessionTerminalSnapshot> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public StreamingProxyChatSessionTerminalProjector(
        IProjectionWriteDispatcher<StreamingProxyChatSessionTerminalSnapshot> writeDispatcher,
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
            state == null ||
            !stateEvent.EventData.Is(StreamingProxyChatSessionTerminalStateChanged.Descriptor))
        {
            return;
        }

        var terminal = stateEvent.EventData.Unpack<StreamingProxyChatSessionTerminalStateChanged>();
        if (string.IsNullOrWhiteSpace(terminal.SessionId) ||
            !state.TerminalSessions.TryGetValue(terminal.SessionId, out var record))
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var snapshot = new StreamingProxyChatSessionTerminalSnapshot
        {
            Id = StreamingProxyChatSessionTerminalQueryPort.ComposeSnapshotId(context.RootActorId, terminal.SessionId),
            ActorId = StreamingProxyChatSessionTerminalQueryPort.ComposeSnapshotId(context.RootActorId, terminal.SessionId),
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            RootActorId = context.RootActorId,
            SessionId = terminal.SessionId,
            Status = record.Status,
            TerminalAt = record.TerminalAt,
            ErrorMessage = record.ErrorMessage ?? string.Empty,
        };

        await _writeDispatcher.UpsertAsync(snapshot, ct);
    }
}
