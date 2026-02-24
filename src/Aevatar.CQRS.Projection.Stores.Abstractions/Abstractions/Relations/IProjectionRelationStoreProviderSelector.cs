namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionRelationStoreProviderSelector
{
    IProjectionStoreRegistration<IProjectionRelationStore> Select(
        IReadOnlyList<IProjectionStoreRegistration<IProjectionRelationStore>> registrations,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements);
}
