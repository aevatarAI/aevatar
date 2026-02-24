namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public sealed class ProjectionStoreDispatchCompensationContext<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel
{
    public required string Operation { get; init; }

    public required string FailedStore { get; init; }

    public required IReadOnlyList<string> SucceededStores { get; init; }

    public required TReadModel ReadModel { get; init; }

    public required Exception Exception { get; init; }

    public TKey? Key { get; init; }
}
