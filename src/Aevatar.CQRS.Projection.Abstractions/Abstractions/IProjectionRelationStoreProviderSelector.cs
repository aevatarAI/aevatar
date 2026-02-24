namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionRelationStoreProviderSelector
{
    IProjectionRelationStoreRegistration Select(
        IReadOnlyList<IProjectionRelationStoreRegistration> registrations,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements);
}
