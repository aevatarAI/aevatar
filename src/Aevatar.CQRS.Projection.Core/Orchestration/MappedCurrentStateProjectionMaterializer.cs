using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed record MappedCurrentStateProjectionInput<TContext, TState>(
    TContext Context,
    EventEnvelope Envelope,
    StateEvent StateEvent,
    TState State,
    DateTimeOffset ObservedAt)
    where TContext : IProjectionMaterializationContext
    where TState : class, IMessage<TState>, new();

/// <summary>
/// Helper for simple actor-owned current-state projectors that map one committed state root to one read model.
/// Complex projectors can keep implementing ICurrentStateProjectionMaterializer directly.
/// </summary>
// Refactor (iter97/cluster-591):
//   Old pattern: each current-state projector hand-wrote committed-state unpack + timestamp resolution + upsert shell
//   New principle: this helper centralizes the projection shell; subclasses only map TState -> TReadModel
public abstract class MappedCurrentStateProjectionMaterializer<TContext, TState, TReadModel>
    : ICurrentStateProjectionMaterializer<TContext>
    where TContext : IProjectionMaterializationContext
    where TState : class, IMessage<TState>, new()
    where TReadModel : class, IProjectionReadModel
{
    private readonly IProjectionWriteDispatcher<TReadModel> _writeDispatcher;
    private readonly IProjectionClock _clock;

    protected MappedCurrentStateProjectionMaterializer(
        IProjectionWriteDispatcher<TReadModel> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        TContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        if (!CommittedStateEventEnvelope.TryUnpackState<TState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var input = new MappedCurrentStateProjectionInput<TContext, TState>(
            context,
            envelope,
            stateEvent,
            state,
            CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow));

        var readModel = Map(input);
        if (readModel == null)
            return;

        await _writeDispatcher.UpsertAsync(readModel, ct);
    }

    protected abstract TReadModel? Map(MappedCurrentStateProjectionInput<TContext, TState> input);
}
