namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionReadModelProviderSelector
{
    IProjectionReadModelStoreRegistration<TReadModel, TKey> Select<TReadModel, TKey>(
        IReadOnlyList<IProjectionReadModelStoreRegistration<TReadModel, TKey>> registrations,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements)
        where TReadModel : class;
}
