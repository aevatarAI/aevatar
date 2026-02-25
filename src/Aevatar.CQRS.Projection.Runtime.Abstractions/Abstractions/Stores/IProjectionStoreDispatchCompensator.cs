namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionStoreDispatchCompensator<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel
{
    Task CompensateAsync(
        ProjectionStoreDispatchCompensationContext<TReadModel, TKey> context,
        CancellationToken ct = default);
}
