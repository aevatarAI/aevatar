using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionDocumentStoreProviderSelector
    : IProjectionDocumentStoreProviderSelector
{
    private readonly ILogger<ProjectionDocumentStoreProviderSelector> _logger;

    public ProjectionDocumentStoreProviderSelector(
        ILogger<ProjectionDocumentStoreProviderSelector>? logger = null)
    {
        _logger = logger ?? NullLogger<ProjectionDocumentStoreProviderSelector>.Instance;
    }

    public IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>> Select<TReadModel, TKey>(
        IReadOnlyList<IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>> registrations,
        ProjectionDocumentSelectionOptions selectionOptions)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(selectionOptions);
        var selected = SelectRegistration(
            registrations,
            selectionOptions,
            typeof(TReadModel),
            "No document store provider registrations were found.",
            "Multiple document store providers are registered but no explicit provider was requested.",
            "Requested document store provider is not registered.");
        _logger.LogInformation(
            "Projection document provider selected. readModel={ReadModel} provider={Provider}",
            typeof(TReadModel).FullName,
            selected.ProviderName);
        return selected;
    }

    private static IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>> SelectRegistration<TReadModel, TKey>(
        IReadOnlyList<IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>> registrations,
        ProjectionDocumentSelectionOptions selectionOptions,
        Type logicalModelType,
        string noRegistrationsReason,
        string multipleRegistrationsReason,
        string providerNotRegisteredReason)
        where TReadModel : class
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
