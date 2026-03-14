namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionStoreDispatcher<TReadModel, in TKey>
    : IProjectionWriteDispatcher<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel;
