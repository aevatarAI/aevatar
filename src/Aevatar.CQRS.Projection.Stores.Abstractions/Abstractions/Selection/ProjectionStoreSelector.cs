namespace Aevatar.CQRS.Projection.Abstractions;

public static class ProjectionStoreSelector
{
    public static TRegistration Select<TRegistration>(
        IEnumerable<TRegistration> registrations,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements,
        Type logicalModelType,
        string noRegistrationsReason,
        string multipleRegistrationsReason,
        string providerNotRegisteredReason,
        IProjectionReadModelCapabilityValidator? capabilityValidator = null)
        where TRegistration : IProjectionStoreRegistration
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(selectionOptions);
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(logicalModelType);

        var candidates = registrations.ToList();
        var requestedProviderName = selectionOptions.RequestedProviderName?.Trim() ?? "";
        if (candidates.Count == 0)
        {
            throw new ProjectionProviderSelectionException(
                logicalModelType,
                requestedProviderName,
                [],
                noRegistrationsReason);
        }

        var selected = ResolveRegistration<TRegistration>(
            candidates,
            requestedProviderName,
            logicalModelType,
            multipleRegistrationsReason,
            providerNotRegisteredReason);
        var violations = capabilityValidator == null
            ? ProjectionReadModelCapabilityValidator.Validate(requirements, selected.Capabilities)
            : capabilityValidator.Validate(requirements, selected.Capabilities);
        if (violations.Count > 0 && selectionOptions.FailOnUnsupportedCapabilities)
        {
            throw new ProjectionReadModelCapabilityValidationException(
                logicalModelType,
                requirements,
                selected.Capabilities,
                violations);
        }

        return selected;
    }

    private static TRegistration ResolveRegistration<TRegistration>(
        IReadOnlyList<TRegistration> registrations,
        string requestedProviderName,
        Type logicalModelType,
        string multipleRegistrationsReason,
        string providerNotRegisteredReason)
        where TRegistration : IProjectionStoreRegistration
    {
        if (requestedProviderName.Length == 0)
        {
            if (registrations.Count == 1)
                return registrations[0];

            throw new ProjectionProviderSelectionException(
                logicalModelType,
                requestedProviderName,
                registrations.Select(x => x.ProviderName).ToList(),
                multipleRegistrationsReason);
        }

        var matched = registrations
            .FirstOrDefault(x => string.Equals(
                x.ProviderName,
                requestedProviderName,
                StringComparison.OrdinalIgnoreCase));
        if (matched != null)
            return matched;

        throw new ProjectionProviderSelectionException(
            logicalModelType,
            requestedProviderName,
            registrations.Select(x => x.ProviderName).ToList(),
            providerNotRegisteredReason);
    }
}
