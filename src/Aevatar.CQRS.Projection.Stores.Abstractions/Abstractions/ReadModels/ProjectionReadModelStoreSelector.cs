namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public static class ProjectionReadModelStoreSelector
{
    public static IProjectionStoreRegistration<IProjectionReadModelStore<TReadModel, TKey>> Select<TReadModel, TKey>(
        IEnumerable<IProjectionStoreRegistration<IProjectionReadModelStore<TReadModel, TKey>>> registrations,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements,
        IProjectionReadModelCapabilityValidator? capabilityValidator = null)
        where TReadModel : class
    {
        return ProjectionStoreSelector.Select<
            IProjectionStoreRegistration<IProjectionReadModelStore<TReadModel, TKey>>>(
            registrations,
            selectionOptions,
            requirements,
            typeof(TReadModel),
            noRegistrationsReason: "No provider registrations were found.",
            multipleRegistrationsReason: "Multiple providers are registered but no explicit provider was requested.",
            providerNotRegisteredReason: "Requested provider is not registered.",
            capabilityValidator);
    }
}
