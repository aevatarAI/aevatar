using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.CQRS.Projection.StateMirror.Abstractions;

public interface IStateMirrorReadModelProjector<TState, TReadModel, in TKey>
    where TState : class
    where TReadModel : class, IProjectionReadModel
{
    TReadModel Project(TState state);

    Task<TReadModel> ProjectAndUpsertAsync(TState state, CancellationToken ct = default);

    Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default);

    Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
        ProjectionDocumentQuery query,
        CancellationToken ct = default);
}
