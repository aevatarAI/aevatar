using Aevatar.CQRS.Sagas.Abstractions.State;

namespace Aevatar.CQRS.Sagas.Abstractions.Runtime;

public static class SagaRepositoryExtensions
{
    public static async Task<TState?> LoadAsync<TState>(
        this ISagaRepository repository,
        string sagaName,
        string correlationId,
        CancellationToken ct = default)
        where TState : class, ISagaState
    {
        ArgumentNullException.ThrowIfNull(repository);

        return await repository.LoadAsync(sagaName, correlationId, typeof(TState), ct) as TState;
    }

    public static Task SaveAsync<TState>(
        this ISagaRepository repository,
        string sagaName,
        TState state,
        int? expectedVersion = null,
        CancellationToken ct = default)
        where TState : class, ISagaState
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(state);

        return repository.SaveAsync(sagaName, state, typeof(TState), expectedVersion, ct);
    }

    public static async Task<IReadOnlyList<TState>> ListAsync<TState>(
        this ISagaRepository repository,
        string sagaName,
        int take = 100,
        CancellationToken ct = default)
        where TState : class, ISagaState
    {
        ArgumentNullException.ThrowIfNull(repository);

        var states = await repository.ListAsync(sagaName, typeof(TState), take, ct);
        return states.OfType<TState>().ToList();
    }
}
