using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.StateMirror.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.CQRS.Projection.StateMirror.Services;

public sealed class StateMirrorReadModelProjector<TState, TReadModel, TKey>
    : IStateMirrorReadModelProjector<TState, TReadModel, TKey>
    where TState : class
    where TReadModel : class, IProjectionReadModel
{
    private readonly IStateMirrorProjection<TState, TReadModel> _projection;
    private readonly IProjectionStoreDispatcher<TReadModel, TKey> _storeDispatcher;

    public StateMirrorReadModelProjector(
        IStateMirrorProjection<TState, TReadModel> projection,
        IProjectionStoreDispatcher<TReadModel, TKey> storeDispatcher)
    {
        _projection = projection;
        _storeDispatcher = storeDispatcher;
    }

    public TReadModel Project(TState state)
    {
        return _projection.Project(state);
    }

    public async Task<TReadModel> ProjectAndUpsertAsync(TState state, CancellationToken ct = default)
    {
        var readModel = Project(state);
        await _storeDispatcher.UpsertAsync(readModel, ct);
        return readModel;
    }

    public Task MutateAsync(TKey key, Action<TReadModel> mutate, CancellationToken ct = default)
    {
        return _storeDispatcher.MutateAsync(key, mutate, ct);
    }

    public Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default)
    {
        return _storeDispatcher.GetAsync(key, ct);
    }

    public Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        return _storeDispatcher.ListAsync(take, ct);
    }
}
