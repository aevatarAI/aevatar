namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionDocumentStore<TReadModel, in TKey>
    : IProjectionDocumentWriter<TReadModel>,
      IProjectionDocumentReader<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel;
