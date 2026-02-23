using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionReadModelProviderSelector
    : IProjectionReadModelProviderSelector
{
    private readonly IProjectionReadModelCapabilityValidator _capabilityValidator;
    private readonly ILogger<ProjectionReadModelProviderSelector> _logger;

    public ProjectionReadModelProviderSelector(
        IProjectionReadModelCapabilityValidator capabilityValidator,
        ILogger<ProjectionReadModelProviderSelector>? logger = null)
    {
        _capabilityValidator = capabilityValidator;
        _logger = logger ?? NullLogger<ProjectionReadModelProviderSelector>.Instance;
    }

    public IProjectionReadModelStoreRegistration<TReadModel, TKey> Select<TReadModel, TKey>(
        IReadOnlyList<IProjectionReadModelStoreRegistration<TReadModel, TKey>> registrations,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(selectionOptions);
        ArgumentNullException.ThrowIfNull(requirements);

        try
        {
            var selected = ProjectionReadModelStoreSelector.Select(
                registrations,
                selectionOptions,
                requirements,
                _capabilityValidator);
            _logger.LogInformation(
                "Projection provider selected. readModel={ReadModel} provider={Provider} failOnUnsupportedCapabilities={FailOnUnsupportedCapabilities}",
                typeof(TReadModel).FullName,
                selected.ProviderName,
                selectionOptions.FailOnUnsupportedCapabilities);
            return selected;
        }
        catch (ProjectionReadModelCapabilityValidationException ex)
        {
            _logger.LogError(
                "Projection provider capability validation failed. readModel={ReadModel} provider={Provider} requiredCapabilities={RequiredCapabilities} actualCapabilities={ActualCapabilities} violations={Violations}",
                typeof(TReadModel).FullName,
                ex.Capabilities.ProviderName,
                FormatRequirements(ex.Requirements),
                FormatCapabilities(ex.Capabilities),
                string.Join("; ", ex.Violations));
            throw;
        }
        catch (ProjectionProviderSelectionException ex)
        {
            var requestedProvider = ex.RequestedProviderName.Length == 0 ? "<unspecified>" : ex.RequestedProviderName;
            var availableProviders = ex.AvailableProviders.Count == 0 ? "<none>" : string.Join(", ", ex.AvailableProviders);
            _logger.LogError(
                "Projection provider selection failed. readModel={ReadModel} requestedProvider={RequestedProvider} availableProviders={AvailableProviders} reason={Reason}",
                typeof(TReadModel).FullName,
                requestedProvider,
                availableProviders,
                ex.Reason);
            throw;
        }
    }

    private static string FormatRequirements(ProjectionReadModelRequirements requirements)
    {
        return $"requiresIndexing={requirements.RequiresIndexing};" +
               $"requiredIndexKinds=[{string.Join(",", requirements.RequiredIndexKinds)}];" +
               $"requiresAliases={requirements.RequiresAliases};" +
               $"requiresSchemaValidation={requirements.RequiresSchemaValidation}";
    }

    private static string FormatCapabilities(ProjectionReadModelProviderCapabilities capabilities)
    {
        return $"supportsIndexing={capabilities.SupportsIndexing};" +
               $"indexKinds=[{string.Join(",", capabilities.IndexKinds)}];" +
               $"supportsAliases={capabilities.SupportsAliases};" +
               $"supportsSchemaValidation={capabilities.SupportsSchemaValidation}";
    }
}
