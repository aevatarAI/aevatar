namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionGraphStartupValidator : IProjectionGraphStartupValidator
{
    private readonly IProjectionGraphStoreProviderRegistry _providerRegistry;
    private readonly IProjectionGraphStoreProviderSelector _providerSelector;

    public ProjectionGraphStartupValidator(
        IProjectionGraphStoreProviderRegistry providerRegistry,
        IProjectionGraphStoreProviderSelector providerSelector)
    {
        _providerRegistry = providerRegistry;
        _providerSelector = providerSelector;
    }

    public IProjectionStoreRegistration<IProjectionGraphStore> ValidateProvider(
        IServiceProvider serviceProvider,
        ProjectionGraphSelectionOptions selectionOptions)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(selectionOptions);

        var registrations = _providerRegistry.GetRegistrations(serviceProvider);
        return _providerSelector.Select(registrations, selectionOptions);
    }
}
