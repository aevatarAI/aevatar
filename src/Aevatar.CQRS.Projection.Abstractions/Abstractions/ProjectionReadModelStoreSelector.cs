namespace Aevatar.CQRS.Projection.Abstractions;

public static class ProjectionReadModelStoreSelector
{
    public static IProjectionReadModelStoreRegistration<TReadModel, TKey> Select<TReadModel, TKey>(
        IEnumerable<IProjectionReadModelStoreRegistration<TReadModel, TKey>> registrations,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(selectionOptions);
        ArgumentNullException.ThrowIfNull(requirements);

        var candidates = registrations.ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"No read-model provider registrations found for '{typeof(TReadModel).FullName}'.");

        var requestedProviderName = selectionOptions.RequestedProviderName?.Trim() ?? "";
        var selected = ResolveRegistration(candidates, requestedProviderName);

        var violations = ProjectionReadModelCapabilityValidator.Validate(requirements, selected.Capabilities);
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

            throw new InvalidOperationException(
                $"Multiple providers are registered for '{typeof(TReadModel).FullName}', but no explicit provider was requested. " +
                $"Available: {string.Join(", ", registrations.Select(x => x.ProviderName))}.");
        }

        var matched = registrations
            .FirstOrDefault(x => string.Equals(
                x.ProviderName,
                requestedProviderName,
                StringComparison.OrdinalIgnoreCase));

        if (matched != null)
            return matched;

        throw new InvalidOperationException(
            $"Requested provider '{requestedProviderName}' is not registered for '{typeof(TReadModel).FullName}'. " +
            $"Available: {string.Join(", ", registrations.Select(x => x.ProviderName))}.");
    }
}
