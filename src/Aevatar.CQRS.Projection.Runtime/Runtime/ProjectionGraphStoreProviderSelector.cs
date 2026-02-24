using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionGraphStoreProviderSelector
    : IProjectionGraphStoreProviderSelector
{
    private readonly IProjectionProviderCapabilityValidator _capabilityValidator;
    private readonly ILogger<ProjectionGraphStoreProviderSelector> _logger;

    public ProjectionGraphStoreProviderSelector(
        IProjectionProviderCapabilityValidator capabilityValidator,
        ILogger<ProjectionGraphStoreProviderSelector>? logger = null)
    {
        _capabilityValidator = capabilityValidator;
        _logger = logger ?? NullLogger<ProjectionGraphStoreProviderSelector>.Instance;
    }

    public IProjectionStoreRegistration<IProjectionGraphStore> Select(
        IReadOnlyList<IProjectionStoreRegistration<IProjectionGraphStore>> registrations,
        ProjectionStoreSelectionOptions selectionOptions,
        ProjectionStoreRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(selectionOptions);
        ArgumentNullException.ThrowIfNull(requirements);

        var selected = ProjectionStoreSelector.Select<IProjectionStoreRegistration<IProjectionGraphStore>>(
            registrations,
            selectionOptions,
            requirements,
            typeof(ProjectionGraphNode),
            noRegistrationsReason: "No relation store provider registrations were found.",
            multipleRegistrationsReason: "Multiple relation store providers are registered but no explicit provider was requested.",
            providerNotRegisteredReason: "Requested relation store provider is not registered.",
            _capabilityValidator);

        _logger.LogInformation(
            "Projection relation provider selected. provider={Provider} failOnUnsupportedCapabilities={FailOnUnsupportedCapabilities}",
            selected.ProviderName,
            selectionOptions.FailOnUnsupportedCapabilities);

        return selected;
    }
}
