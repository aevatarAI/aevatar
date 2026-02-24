namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionReadModelProviderSelector
{
    IProjectionStoreRegistration<IProjectionReadModelStore<TReadModel, TKey>> Select<TReadModel, TKey>(
        IReadOnlyList<IProjectionStoreRegistration<IProjectionReadModelStore<TReadModel, TKey>>> registrations,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements)
        where TReadModel : class;
}
