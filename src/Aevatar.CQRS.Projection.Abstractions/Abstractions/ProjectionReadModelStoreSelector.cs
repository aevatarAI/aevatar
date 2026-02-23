namespace Aevatar.CQRS.Projection.Abstractions;

public static class ProjectionReadModelStoreSelector
{
    public static IProjectionReadModelStoreRegistration<TReadModel, TKey> Select<TReadModel, TKey>(
        IEnumerable<IProjectionReadModelStoreRegistration<TReadModel, TKey>> registrations,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements,
        IProjectionReadModelCapabilityValidator? capabilityValidator = null)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(selectionOptions);
        ArgumentNullException.ThrowIfNull(requirements);

        var candidates = registrations.ToList();
        var requestedProviderName = selectionOptions.RequestedProviderName?.Trim() ?? "";
        if (candidates.Count == 0)
        {
            throw new ProjectionProviderSelectionException(
                typeof(TReadModel),
                requestedProviderName,
                [],
                "No provider registrations were found.");
        }

        var selected = ResolveRegistration(candidates, requestedProviderName);

        var violations = capabilityValidator == null
            ? ProjectionReadModelCapabilityValidator.Validate(requirements, selected.Capabilities)
            : capabilityValidator.Validate(requirements, selected.Capabilities);
        if (violations.Count > 0 && selectionOptions.FailOnUnsupportedCapabilities)
        {
            throw new ProjectionReadModelCapabilityValidationException(
                typeof(TReadModel),
                requirements,
                selected.Capabilities,
                violations);
        }

        return selected;
    }

    private static IProjectionReadModelStoreRegistration<TReadModel, TKey> ResolveRegistration<TReadModel, TKey>(
        IReadOnlyList<IProjectionReadModelStoreRegistration<TReadModel, TKey>> registrations,
        string requestedProviderName)
        where TReadModel : class
    {
        if (requestedProviderName.Length == 0)
        {
            if (registrations.Count == 1)
                return registrations[0];

            throw new ProjectionProviderSelectionException(
                typeof(TReadModel),
                requestedProviderName,
                registrations.Select(x => x.ProviderName).ToList(),
                "Multiple providers are registered but no explicit provider was requested.");
        }

        var matched = registrations
            .FirstOrDefault(x => string.Equals(
                x.ProviderName,
                requestedProviderName,
                StringComparison.OrdinalIgnoreCase));

        if (matched != null)
            return matched;

        throw new ProjectionProviderSelectionException(
            typeof(TReadModel),
            requestedProviderName,
            registrations.Select(x => x.ProviderName).ToList(),
            "Requested provider is not registered.");
    }
}
