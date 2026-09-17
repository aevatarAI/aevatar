namespace Aevatar.CQRS.Projection.Stores.Abstractions;

/// <summary>
/// Atomically reduces one projection document from its current stored value.
/// The reducer can be invoked more than once after an optimistic-concurrency
/// conflict, so it must be deterministic and free of external side effects.
/// </summary>
public interface IProjectionDocumentMutator<TReadModel, in TKey>
    where TReadModel : class, IProjectionReadModel<TReadModel>, new()
{
    Task<ProjectionDocumentMutationResult<TReadModel>> MutateAsync(
        TKey key,
        Func<TReadModel?, TReadModel> reducer,
        CancellationToken ct = default);
}

public sealed record ProjectionDocumentMutationResult<TReadModel>(
    ProjectionWriteResult WriteResult,
    TReadModel? Document)
    where TReadModel : class, IProjectionReadModel;
