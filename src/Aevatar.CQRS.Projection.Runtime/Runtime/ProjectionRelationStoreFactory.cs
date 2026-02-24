using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionRelationStoreFactory : IProjectionRelationStoreFactory
{
    private readonly IProjectionRelationStoreProviderRegistry _providerRegistry;
    private readonly IProjectionRelationStoreProviderSelector _providerSelector;
    private readonly ILogger<ProjectionRelationStoreFactory> _logger;

    public ProjectionRelationStoreFactory(
        IProjectionRelationStoreProviderRegistry providerRegistry,
        IProjectionRelationStoreProviderSelector providerSelector,
        ILogger<ProjectionRelationStoreFactory>? logger = null)
    {
        _providerRegistry = providerRegistry;
        _providerSelector = providerSelector;
        _logger = logger ?? NullLogger<ProjectionRelationStoreFactory>.Instance;
    }

    public IProjectionRelationStore Create(
        IServiceProvider serviceProvider,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(selectionOptions);
        ArgumentNullException.ThrowIfNull(requirements);

        var registrations = _providerRegistry.GetRegistrations(serviceProvider);
        var selected = _providerSelector.Select(registrations, selectionOptions, requirements);

        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var store = selected.Create(serviceProvider);
            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogInformation(
                "Projection relation store created. provider={Provider} elapsedMs={ElapsedMs} result={Result}",
                selected.ProviderName,
                elapsedMs,
                "ok");
            return store;
        }
        catch (Exception ex)
        {
            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogError(
                ex,
                "Projection relation store creation failed. provider={Provider} elapsedMs={ElapsedMs} result={Result} errorType={ErrorType}",
                selected.ProviderName,
                elapsedMs,
                "failed",
                ex.GetType().Name);
            throw;
        }
    }
}
