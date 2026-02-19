using Aevatar.CQRS.Sagas.Abstractions.State;

namespace Aevatar.CQRS.Sagas.Abstractions.Runtime;

public abstract class SagaBase<TState> : ISaga
    where TState : class, ISagaState, new()
{
    public abstract string Name { get; }

    public Type StateType => typeof(TState);

    public abstract ValueTask<bool> CanHandleAsync(EventEnvelope envelope, CancellationToken ct = default);

    public virtual ValueTask<bool> CanStartAsync(EventEnvelope envelope, CancellationToken ct = default) =>
        CanHandleAsync(envelope, ct);

    public ISagaState CreateNewState(string correlationId, EventEnvelope envelope)
    {
        var state = CreateState(correlationId, envelope);
        if (string.IsNullOrWhiteSpace(state.SagaId))
            state.SagaId = Guid.NewGuid().ToString("N");
        state.CorrelationId = correlationId;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        return state;
    }

    public ValueTask HandleAsync(
        ISagaState state,
        EventEnvelope envelope,
        ISagaActionSink actions,
        CancellationToken ct = default)
    {
        if (state is not TState typedState)
            throw new InvalidOperationException(
                $"Saga '{Name}' expected state type '{typeof(TState).FullName}', but got '{state.GetType().FullName}'.");

        return HandleAsync(typedState, envelope, actions, ct);
    }

    protected virtual TState CreateState(string correlationId, EventEnvelope envelope)
    {
        var state = new TState
        {
            CorrelationId = correlationId,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        if (string.IsNullOrWhiteSpace(state.SagaId))
            state.SagaId = Guid.NewGuid().ToString("N");

        return state;
    }

    protected abstract ValueTask HandleAsync(
        TState state,
        EventEnvelope envelope,
        ISagaActionSink actions,
        CancellationToken ct = default);
}
