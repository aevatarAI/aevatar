using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionDocumentStoreProviderSelector
    : IProjectionDocumentStoreProviderSelector
{
    private readonly IProjectionProviderCapabilityValidator _capabilityValidator;
    private readonly ILogger<ProjectionDocumentStoreProviderSelector> _logger;

    public ProjectionDocumentStoreProviderSelector(
        IProjectionProviderCapabilityValidator capabilityValidator,
        ILogger<ProjectionDocumentStoreProviderSelector>? logger = null)
    {
        _capabilityValidator = capabilityValidator;
        _logger = logger ?? NullLogger<ProjectionDocumentStoreProviderSelector>.Instance;
    }

    public IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>> Select<TReadModel, TKey>(
        IReadOnlyList<IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>> registrations,
        ProjectionStoreSelectionOptions selectionOptions,
        ProjectionStoreRequirements requirements)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(selectionOptions);
        ArgumentNullException.ThrowIfNull(requirements);

        try
        {
            var selected = ProjectionDocumentStoreSelector.Select(
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
        catch (ProjectionProviderCapabilityValidationException ex)
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

    private static string FormatRequirements(ProjectionStoreRequirements requirements)
    {
        return $"requiresIndexing={requirements.RequiresIndexing};" +
               $"requiredIndexKinds=[{string.Join(",", requirements.RequiredIndexKinds)}];" +
               $"requiresAliases={requirements.RequiresAliases};" +
               $"requiresSchemaValidation={requirements.RequiresSchemaValidation};" +
               $"requiresGraph={requirements.RequiresGraph};" +
               $"requiresGraphTraversal={requirements.RequiresGraphTraversal}";
    }

    private static string FormatCapabilities(ProjectionProviderCapabilities capabilities)
    {
        return $"supportsIndexing={capabilities.SupportsIndexing};" +
               $"indexKinds=[{string.Join(",", capabilities.IndexKinds)}];" +
               $"supportsAliases={capabilities.SupportsAliases};" +
               $"supportsSchemaValidation={capabilities.SupportsSchemaValidation};" +
               $"supportsGraph={capabilities.SupportsGraph};" +
               $"supportsGraphTraversal={capabilities.SupportsGraphTraversal}";
    }
}
