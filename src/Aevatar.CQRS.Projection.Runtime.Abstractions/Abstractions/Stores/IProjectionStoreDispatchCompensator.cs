namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionStoreDispatchCompensator<TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    Task CompensateAsync(
        ProjectionStoreDispatchCompensationContext<TReadModel> context,
        CancellationToken ct = default);
}
