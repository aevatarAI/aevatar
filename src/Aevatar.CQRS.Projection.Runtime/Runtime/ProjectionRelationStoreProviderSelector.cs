using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionRelationStoreProviderSelector
    : IProjectionRelationStoreProviderSelector
{
    private readonly IProjectionReadModelCapabilityValidator _capabilityValidator;
    private readonly ILogger<ProjectionRelationStoreProviderSelector> _logger;

    public ProjectionRelationStoreProviderSelector(
        IProjectionReadModelCapabilityValidator capabilityValidator,
        ILogger<ProjectionRelationStoreProviderSelector>? logger = null)
    {
        _capabilityValidator = capabilityValidator;
        _logger = logger ?? NullLogger<ProjectionRelationStoreProviderSelector>.Instance;
    }

    public IProjectionStoreRegistration<IProjectionRelationStore> Select(
        IReadOnlyList<IProjectionStoreRegistration<IProjectionRelationStore>> registrations,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(selectionOptions);
        ArgumentNullException.ThrowIfNull(requirements);

        var selected = ProjectionStoreSelector.Select<IProjectionStoreRegistration<IProjectionRelationStore>>(
            registrations,
            selectionOptions,
            requirements,
            typeof(ProjectionRelationNode),
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
