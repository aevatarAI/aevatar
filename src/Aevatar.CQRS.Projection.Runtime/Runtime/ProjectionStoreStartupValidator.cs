namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionStoreStartupValidator : IProjectionStoreStartupValidator
{
    private readonly IProjectionDocumentStoreProviderRegistry _readModelProviderRegistry;
    private readonly IProjectionDocumentStoreProviderSelector _readModelProviderSelector;
    private readonly IProjectionGraphStoreProviderRegistry _graphProviderRegistry;
    private readonly IProjectionGraphStoreProviderSelector _graphProviderSelector;

    public ProjectionStoreStartupValidator(
        IProjectionDocumentStoreProviderRegistry readModelProviderRegistry,
        IProjectionDocumentStoreProviderSelector readModelProviderSelector,
        IProjectionGraphStoreProviderRegistry graphProviderRegistry,
        IProjectionGraphStoreProviderSelector graphProviderSelector)
    {
        _readModelProviderRegistry = readModelProviderRegistry;
        _readModelProviderSelector = readModelProviderSelector;
        _graphProviderRegistry = graphProviderRegistry;
        _graphProviderSelector = graphProviderSelector;
    }

    public IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>> ValidateDocumentProvider<TReadModel, TKey>(
        IServiceProvider serviceProvider,
        ProjectionStoreSelectionOptions selectionOptions,
        ProjectionStoreRequirements requirements)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(selectionOptions);
        ArgumentNullException.ThrowIfNull(requirements);

        var registrations = _readModelProviderRegistry.GetRegistrations<TReadModel, TKey>(serviceProvider);
        return _readModelProviderSelector.Select(registrations, selectionOptions, requirements);
    }

    public IProjectionStoreRegistration<IProjectionGraphStore> ValidateGraphProvider(
        IServiceProvider serviceProvider,
        ProjectionStoreSelectionOptions selectionOptions,
        ProjectionStoreRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(selectionOptions);
        ArgumentNullException.ThrowIfNull(requirements);

        var registrations = _graphProviderRegistry.GetRegistrations(serviceProvider);
        return _graphProviderSelector.Select(registrations, selectionOptions, requirements);
    }
}
