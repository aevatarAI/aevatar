using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionReadModelStoreFactory
    : IProjectionReadModelStoreFactory
{
    private readonly IProjectionReadModelProviderRegistry _providerRegistry;
    private readonly IProjectionReadModelProviderSelector _providerSelector;
    private readonly ILogger<ProjectionReadModelStoreFactory> _logger;

    public ProjectionReadModelStoreFactory(
        IProjectionReadModelProviderRegistry providerRegistry,
        IProjectionReadModelProviderSelector providerSelector,
        ILogger<ProjectionReadModelStoreFactory>? logger = null)
    {
        _providerRegistry = providerRegistry;
        _providerSelector = providerSelector;
        _logger = logger ?? NullLogger<ProjectionReadModelStoreFactory>.Instance;
    }

    public IProjectionReadModelStore<TReadModel, TKey> Create<TReadModel, TKey>(
        IServiceProvider serviceProvider,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(selectionOptions);
        ArgumentNullException.ThrowIfNull(requirements);

        var registrations = _providerRegistry.GetRegistrations<TReadModel, TKey>(serviceProvider);
        var selected = _providerSelector.Select(registrations, selectionOptions, requirements);

        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var store = selected.Create(serviceProvider);
            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogInformation(
                "Projection read-model store created. provider={Provider} readModelType={ReadModelType} elapsedMs={ElapsedMs} result={Result}",
                selected.ProviderName,
                typeof(TReadModel).FullName,
                elapsedMs,
                "ok");
            return store;
        }
        catch (Exception ex)
        {
            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogError(
                ex,
                "Projection read-model store creation failed. provider={Provider} readModelType={ReadModelType} elapsedMs={ElapsedMs} result={Result} errorType={ErrorType}",
                selected.ProviderName,
                typeof(TReadModel).FullName,
                elapsedMs,
                "failed",
                ex.GetType().Name);
            throw;
        }
    }
}
