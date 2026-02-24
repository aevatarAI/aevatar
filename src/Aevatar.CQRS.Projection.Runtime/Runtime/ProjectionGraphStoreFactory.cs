using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionGraphStoreFactory : IProjectionGraphStoreFactory
{
    private readonly ILogger<ProjectionGraphStoreFactory> _logger;

    public ProjectionGraphStoreFactory(
        ILogger<ProjectionGraphStoreFactory>? logger = null)
    {
        _logger = logger ?? NullLogger<ProjectionGraphStoreFactory>.Instance;
    }

    public IProjectionGraphStore Create(
        IServiceProvider serviceProvider,
        string? requestedProviderName = null)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var registrations = serviceProvider
            .GetServices<IProjectionStoreRegistration<IProjectionGraphStore>>()
            .ToList();
        var selected = ProjectionStoreRegistrationSelector.Select(
            registrations,
            requestedProviderName,
            typeof(ProjectionGraphNode),
            noRegistrationsReason: "No relation store provider registrations were found.",
            multipleRegistrationsReason: "Multiple relation store providers are registered but no explicit provider was requested.",
            providerNotRegisteredReason: "Requested relation store provider is not registered.");

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
