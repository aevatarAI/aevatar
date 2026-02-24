using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionDocumentStoreFactory
    : IProjectionDocumentStoreFactory
{
    private readonly IProjectionDocumentStoreProviderRegistry _providerRegistry;
    private readonly IProjectionDocumentStoreProviderSelector _providerSelector;
    private readonly ILogger<ProjectionDocumentStoreFactory> _logger;

    public ProjectionDocumentStoreFactory(
        IProjectionDocumentStoreProviderRegistry providerRegistry,
        IProjectionDocumentStoreProviderSelector providerSelector,
        ILogger<ProjectionDocumentStoreFactory>? logger = null)
    {
        _providerRegistry = providerRegistry;
        _providerSelector = providerSelector;
        _logger = logger ?? NullLogger<ProjectionDocumentStoreFactory>.Instance;
    }

    public IDocumentProjectionStore<TReadModel, TKey> Create<TReadModel, TKey>(
        IServiceProvider serviceProvider,
        ProjectionDocumentSelectionOptions selectionOptions)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(selectionOptions);

        var registrations = _providerRegistry.GetRegistrations<TReadModel, TKey>(serviceProvider);
        var selected = _providerSelector.Select(registrations, selectionOptions);

        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var store = selected.Create(serviceProvider);
            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogInformation(
                "Projection document store created. provider={Provider} readModelType={ReadModelType} elapsedMs={ElapsedMs} result={Result}",
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
                "Projection document store creation failed. provider={Provider} readModelType={ReadModelType} elapsedMs={ElapsedMs} result={Result} errorType={ErrorType}",
                selected.ProviderName,
                typeof(TReadModel).FullName,
                elapsedMs,
                "failed",
                ex.GetType().Name);
            throw;
        }
    }
}
