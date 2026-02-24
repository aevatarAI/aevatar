using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionDocumentStoreFactory
    : IProjectionDocumentStoreFactory
{
    private readonly ILogger<ProjectionDocumentStoreFactory> _logger;

    public ProjectionDocumentStoreFactory(
        ILogger<ProjectionDocumentStoreFactory>? logger = null)
    {
        _logger = logger ?? NullLogger<ProjectionDocumentStoreFactory>.Instance;
    }

    public IDocumentProjectionStore<TReadModel, TKey> Create<TReadModel, TKey>(
        IServiceProvider serviceProvider,
        string? requestedProviderName = null)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var registrations = serviceProvider
            .GetServices<IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>>()
            .ToList();
        var selected = ProjectionStoreRegistrationSelector.Select(
            registrations,
            requestedProviderName,
            typeof(TReadModel),
            noRegistrationsReason: "No document store provider registrations were found.",
            multipleRegistrationsReason: "Multiple document store providers are registered but no explicit provider was requested.",
            providerNotRegisteredReason: "Requested document store provider is not registered.");

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
