namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public static class ProjectionDocumentStoreSelector
{
    public static IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>> Select<TReadModel, TKey>(
        IEnumerable<IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>> registrations,
        ProjectionStoreSelectionOptions selectionOptions,
        ProjectionStoreRequirements requirements,
        IProjectionProviderCapabilityValidator? capabilityValidator = null)
        where TReadModel : class
    {
        return ProjectionStoreSelector.Select<
            IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>>(
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
