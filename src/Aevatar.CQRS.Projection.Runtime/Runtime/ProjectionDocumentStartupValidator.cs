namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionDocumentStartupValidator : IProjectionDocumentStartupValidator
{
    private readonly IProjectionDocumentStoreProviderRegistry _providerRegistry;
    private readonly IProjectionDocumentStoreProviderSelector _providerSelector;

    public ProjectionDocumentStartupValidator(
        IProjectionDocumentStoreProviderRegistry providerRegistry,
        IProjectionDocumentStoreProviderSelector providerSelector)
    {
        _providerRegistry = providerRegistry;
        _providerSelector = providerSelector;
    }

    public IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>> ValidateProvider<TReadModel, TKey>(
        IServiceProvider serviceProvider,
        ProjectionDocumentSelectionOptions selectionOptions)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(selectionOptions);

        var registrations = _providerRegistry.GetRegistrations<TReadModel, TKey>(serviceProvider);
        return _providerSelector.Select(registrations, selectionOptions);
    }
}
