using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionGraphStoreFactory : IProjectionGraphStoreFactory
{
    private readonly IProjectionGraphStoreProviderRegistry _providerRegistry;
    private readonly IProjectionGraphStoreProviderSelector _providerSelector;
    private readonly ILogger<ProjectionGraphStoreFactory> _logger;

    public ProjectionGraphStoreFactory(
        IProjectionGraphStoreProviderRegistry providerRegistry,
        IProjectionGraphStoreProviderSelector providerSelector,
        ILogger<ProjectionGraphStoreFactory>? logger = null)
    {
        _providerRegistry = providerRegistry;
        _providerSelector = providerSelector;
        _logger = logger ?? NullLogger<ProjectionGraphStoreFactory>.Instance;
    }

    public IProjectionGraphStore Create(
        IServiceProvider serviceProvider,
        ProjectionStoreSelectionOptions selectionOptions,
        ProjectionStoreRequirements requirements)
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
