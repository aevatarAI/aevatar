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

    public IProjectionRelationStoreRegistration Select(
        IReadOnlyList<IProjectionRelationStoreRegistration> registrations,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(selectionOptions);
        ArgumentNullException.ThrowIfNull(requirements);

        var requestedProviderName = selectionOptions.RequestedProviderName?.Trim() ?? "";
        if (registrations.Count == 0)
        {
            throw new ProjectionProviderSelectionException(
                typeof(ProjectionRelationNode),
                requestedProviderName,
                [],
                "No relation store provider registrations were found.");
        }

        IProjectionRelationStoreRegistration selected;
        if (requestedProviderName.Length == 0)
        {
            if (registrations.Count != 1)
            {
                throw new ProjectionProviderSelectionException(
                    typeof(ProjectionRelationNode),
                    requestedProviderName,
                    registrations.Select(x => x.ProviderName).ToList(),
                    "Multiple relation store providers are registered but no explicit provider was requested.");
            }

            selected = registrations[0];
        }
        else
        {
            selected = registrations.FirstOrDefault(x =>
                    string.Equals(x.ProviderName, requestedProviderName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ProjectionProviderSelectionException(
                    typeof(ProjectionRelationNode),
                    requestedProviderName,
                    registrations.Select(x => x.ProviderName).ToList(),
                    "Requested relation store provider is not registered.");
        }

        var violations = _capabilityValidator.Validate(requirements, selected.Capabilities);
        if (violations.Count > 0 && selectionOptions.FailOnUnsupportedCapabilities)
        {
            throw new ProjectionReadModelCapabilityValidationException(
                typeof(ProjectionRelationNode),
                requirements,
                selected.Capabilities,
                violations);
        }

        _logger.LogInformation(
            "Projection relation provider selected. provider={Provider} failOnUnsupportedCapabilities={FailOnUnsupportedCapabilities}",
            selected.ProviderName,
            selectionOptions.FailOnUnsupportedCapabilities);

        return selected;
    }
}
