using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Interop.A2A.Abstractions;

namespace Aevatar.Interop.A2A.Application;

// Refactor (iter30/cluster-031-a2a-actor-owned):
//   Old pattern: task current state lived in IA2ATaskStore process memory.
//   New principle: Projection Pipeline materializes current-state readmodel from committed task actor state.
public sealed class A2ATaskCurrentStateProjector
    : ICurrentStateProjectionMaterializer<A2ATaskProjectionContext>
{
    private readonly IProjectionWriteDispatcher<A2ATaskCurrentStateReadModel> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public A2ATaskCurrentStateProjector(
        IProjectionWriteDispatcher<A2ATaskCurrentStateReadModel> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        A2ATaskProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<A2ATaskState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var actorId = stateEvent.AgentId;
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(state.TaskId))
            return;

        var observedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var document = new A2ATaskCurrentStateReadModel
        {
            Id = actorId,
            ActorId = actorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAtUtcValue = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(observedAt),
            State = state,
        };

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
