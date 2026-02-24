namespace Aevatar.CQRS.Projection.Runtime.Runtime;

internal static class ProjectionStoreRegistrationSelector
{
    public static IProjectionStoreRegistration<TStore> Select<TStore>(
        IReadOnlyList<IProjectionStoreRegistration<TStore>> registrations,
        string? requestedProviderName,
        Type logicalModelType,
        string noRegistrationsReason,
        string multipleRegistrationsReason,
        string providerNotRegisteredReason)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(logicalModelType);

        var requestedName = requestedProviderName?.Trim() ?? "";
        if (registrations.Count == 0)
        {
            throw new ProjectionProviderSelectionException(
                logicalModelType,
                requestedName,
                [],
                noRegistrationsReason);
        }

        if (requestedName.Length == 0)
        {
            if (registrations.Count == 1)
                return registrations[0];

            throw new ProjectionProviderSelectionException(
                logicalModelType,
                requestedName,
                registrations.Select(x => x.ProviderName).ToList(),
                multipleRegistrationsReason);
        }

        var matched = registrations
            .FirstOrDefault(x => string.Equals(
                x.ProviderName,
                requestedName,
                StringComparison.OrdinalIgnoreCase));
        if (matched != null)
            return matched;

        throw new ProjectionProviderSelectionException(
            logicalModelType,
            requestedName,
            registrations.Select(x => x.ProviderName).ToList(),
            providerNotRegisteredReason);
    }
}
