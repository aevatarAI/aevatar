namespace Aevatar.CQRS.Projection.Stores.Abstractions;

/// <summary>
/// Generic read-model store contract for projection state persistence/query.
/// </summary>
public interface IProjectionReadModelStore<TReadModel, in TKey>
    : IDocumentProjectionStore<TReadModel, TKey>
    where TReadModel : class;
