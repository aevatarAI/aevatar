namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionRelationStoreProviderSelector
{
    IProjectionStoreRegistration<IProjectionRelationStore> Select(
        IReadOnlyList<IProjectionStoreRegistration<IProjectionRelationStore>> registrations,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements);
}
