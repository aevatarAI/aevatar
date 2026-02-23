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

        if (registrations.Count == 0)
        {
            throw new ProjectionProviderSelectionException(
                typeof(TReadModel),
                selectionOptions.RequestedProviderName?.Trim() ?? "",
                [],
                "No provider registrations were found.");
        }

        var requestedProvider = selectionOptions.RequestedProviderName?.Trim() ?? "";
        var selected = ResolveRegistration(registrations, requestedProvider);
        var violations = _capabilityValidator.Validate(requirements, selected.Capabilities);
        if (violations.Count > 0 && selectionOptions.FailOnUnsupportedCapabilities)
        {
            _logger.LogError(
                "Projection provider capability validation failed. readModel={ReadModel} provider={Provider} requiredCapabilities={RequiredCapabilities} actualCapabilities={ActualCapabilities} violations={Violations}",
                typeof(TReadModel).FullName,
                selected.ProviderName,
                FormatRequirements(requirements),
                FormatCapabilities(selected.Capabilities),
                string.Join("; ", violations));
            throw new ProjectionReadModelCapabilityValidationException(
                typeof(TReadModel),
                requirements,
                selected.Capabilities,
                violations);
        }

        _logger.LogInformation(
            "Projection provider selected. readModel={ReadModel} provider={Provider} failOnUnsupportedCapabilities={FailOnUnsupportedCapabilities}",
            typeof(TReadModel).FullName,
            selected.ProviderName,
            selectionOptions.FailOnUnsupportedCapabilities);
        return selected;
    }

    private static IProjectionReadModelStoreRegistration<TReadModel, TKey> ResolveRegistration<TReadModel, TKey>(
        IReadOnlyList<IProjectionReadModelStoreRegistration<TReadModel, TKey>> registrations,
        string requestedProvider)
        where TReadModel : class
    {
        if (requestedProvider.Length == 0)
        {
            if (registrations.Count == 1)
                return registrations[0];

            throw new ProjectionProviderSelectionException(
                typeof(TReadModel),
                requestedProvider,
                registrations.Select(x => x.ProviderName).ToList(),
                "Multiple providers are registered but no explicit provider was requested.");
        }

        var matched = registrations
            .FirstOrDefault(x => string.Equals(
                x.ProviderName,
                requestedProvider,
                StringComparison.OrdinalIgnoreCase));

        if (matched != null)
            return matched;

        throw new ProjectionProviderSelectionException(
            typeof(TReadModel),
            requestedProvider,
            registrations.Select(x => x.ProviderName).ToList(),
            "Requested provider is not registered.");
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
