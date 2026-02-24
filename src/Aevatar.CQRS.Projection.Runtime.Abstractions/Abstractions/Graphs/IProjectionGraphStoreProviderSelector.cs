namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionGraphStoreProviderSelector
{
    IProjectionStoreRegistration<IProjectionGraphStore> Select(
        IReadOnlyList<IProjectionStoreRegistration<IProjectionGraphStore>> registrations,
        ProjectionGraphSelectionOptions selectionOptions);
}
