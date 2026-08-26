using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

internal sealed class ConnectorCatalogNameAuthority : IConnectorCatalogNameAuthority
{
    private readonly IReadOnlyList<StoredConnectorDefinition> _hostConnectorDefaults;
    private readonly HashSet<string> _hostConnectorNames;

    public ConnectorCatalogNameAuthority(IEnumerable<IHostConnectorCatalogDefaults> hostConnectorDefaults)
    {
        ArgumentNullException.ThrowIfNull(hostConnectorDefaults);

        _hostConnectorDefaults = hostConnectorDefaults
            .SelectMany(static defaults => defaults.Connectors)
            .ToArray();
        _hostConnectorNames = _hostConnectorDefaults
            .Select(static connector => NormalizeRequiredName(connector.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<StoredConnectorDefinition> ComposeDefinitions(
        IReadOnlyList<StoredConnectorDefinition> scopedConnectors) =>
        ComposeByName(
            scopedConnectors,
            static connector => connector.Name,
            static connector => connector);

    public IReadOnlyList<string> ComposeEnabledNames(
        IReadOnlyList<ConnectorCatalogNameEntry> scopedConnectors) =>
        ComposeByName(
                scopedConnectors,
                static connector => connector.Name,
                static connector => new ConnectorCatalogNameEntry(connector.Name, connector.Enabled))
            .Where(static connector => connector.Enabled && !string.IsNullOrWhiteSpace(connector.Name))
            .Select(static connector => connector.Name.Trim())
            .ToArray();

    public IReadOnlyList<StoredConnectorDefinition> SelectScopeOwnedDefinitions(
        IReadOnlyList<StoredConnectorDefinition> requestedConnectors)
    {
        ArgumentNullException.ThrowIfNull(requestedConnectors);

        return requestedConnectors
            .Where(connector => !_hostConnectorNames.Contains(connector.Name.Trim()))
            .ToArray();
    }

    private IReadOnlyList<T> ComposeByName<T>(
        IReadOnlyList<T> scopedConnectors,
        Func<T, string> nameSelector,
        Func<StoredConnectorDefinition, T> hostConnectorSelector)
    {
        ArgumentNullException.ThrowIfNull(scopedConnectors);

        var merged = scopedConnectors.ToList();
        foreach (var hostConnector in _hostConnectorDefaults)
        {
            var hostConnectorName = NormalizeRequiredName(hostConnector.Name);
            var existingIndex = merged.FindIndex(connector =>
                string.Equals(
                    nameSelector(connector).Trim(),
                    hostConnectorName,
                    StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                merged[existingIndex] = hostConnectorSelector(hostConnector);
            else
                merged.Add(hostConnectorSelector(hostConnector));
        }

        return merged.AsReadOnly();
    }

    private static string NormalizeRequiredName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Host connector catalog defaults require a name.");

        return name.Trim();
    }
}
