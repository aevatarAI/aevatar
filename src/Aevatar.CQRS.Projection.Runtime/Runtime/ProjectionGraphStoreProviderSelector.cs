using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionGraphStoreProviderSelector
    : IProjectionGraphStoreProviderSelector
{
    private readonly ILogger<ProjectionGraphStoreProviderSelector> _logger;

    public ProjectionGraphStoreProviderSelector(
        ILogger<ProjectionGraphStoreProviderSelector>? logger = null)
    {
        _logger = logger ?? NullLogger<ProjectionGraphStoreProviderSelector>.Instance;
    }

    public IProjectionStoreRegistration<IProjectionGraphStore> Select(
        IReadOnlyList<IProjectionStoreRegistration<IProjectionGraphStore>> registrations,
        ProjectionGraphSelectionOptions selectionOptions)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(selectionOptions);

        var selected = SelectRegistration(
            registrations,
            selectionOptions,
            typeof(ProjectionGraphNode),
            noRegistrationsReason: "No relation store provider registrations were found.",
            multipleRegistrationsReason: "Multiple relation store providers are registered but no explicit provider was requested.",
            providerNotRegisteredReason: "Requested relation store provider is not registered.");

        _logger.LogInformation(
            "Projection relation provider selected. provider={Provider}",
            selected.ProviderName);

        return selected;
    }

    private static IProjectionStoreRegistration<IProjectionGraphStore> SelectRegistration(
        IReadOnlyList<IProjectionStoreRegistration<IProjectionGraphStore>> registrations,
        ProjectionGraphSelectionOptions selectionOptions,
        Type logicalModelType,
        string noRegistrationsReason,
        string multipleRegistrationsReason,
        string providerNotRegisteredReason)
    {
        var requestedProviderName = selectionOptions.RequestedProviderName?.Trim() ?? "";
        if (registrations.Count == 0)
        {
            throw new ProjectionProviderSelectionException(
                logicalModelType,
                requestedProviderName,
                [],
                noRegistrationsReason);
        }

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
