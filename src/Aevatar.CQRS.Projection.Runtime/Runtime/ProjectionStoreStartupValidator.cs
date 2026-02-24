namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionStoreStartupValidator : IProjectionStoreStartupValidator
{
    private readonly IProjectionReadModelProviderRegistry _readModelProviderRegistry;
    private readonly IProjectionReadModelProviderSelector _readModelProviderSelector;
    private readonly IProjectionRelationStoreProviderRegistry _relationProviderRegistry;
    private readonly IProjectionRelationStoreProviderSelector _relationProviderSelector;

    public ProjectionStoreStartupValidator(
        IProjectionReadModelProviderRegistry readModelProviderRegistry,
        IProjectionReadModelProviderSelector readModelProviderSelector,
        IProjectionRelationStoreProviderRegistry relationProviderRegistry,
        IProjectionRelationStoreProviderSelector relationProviderSelector)
    {
        _readModelProviderRegistry = readModelProviderRegistry;
        _readModelProviderSelector = readModelProviderSelector;
        _relationProviderRegistry = relationProviderRegistry;
        _relationProviderSelector = relationProviderSelector;
    }

    public IProjectionStoreRegistration<IProjectionReadModelStore<TReadModel, TKey>> ValidateReadModelProvider<TReadModel, TKey>(
        IServiceProvider serviceProvider,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(selectionOptions);
        ArgumentNullException.ThrowIfNull(requirements);

        var registrations = _readModelProviderRegistry.GetRegistrations<TReadModel, TKey>(serviceProvider);
        return _readModelProviderSelector.Select(registrations, selectionOptions, requirements);
    }

    public IProjectionStoreRegistration<IProjectionRelationStore> ValidateRelationProvider(
        IServiceProvider serviceProvider,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(selectionOptions);
        ArgumentNullException.ThrowIfNull(requirements);

        var registrations = _relationProviderRegistry.GetRegistrations(serviceProvider);
        return _relationProviderSelector.Select(registrations, selectionOptions, requirements);
    }
}
