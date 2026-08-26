namespace Aevatar.Studio.Application.Studio.Abstractions;

// Refactor (iter56/cluster-911-studio-store-query-command):
//   old=Store mixed read/write + hand-built EventEnvelope
//   new=split query/command port + CQRS Core dispatch
public interface IConnectorCatalogQueryPort
{
    Task<StoredConnectorCatalog> GetConnectorCatalogAsync(CancellationToken cancellationToken = default);

    Task<StoredConnectorDraft> GetConnectorDraftAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Connector definitions owned by the composed Host and published in every Studio scope catalog.
/// </summary>
public interface IHostConnectorCatalogDefaults
{
    IReadOnlyList<StoredConnectorDefinition> Connectors { get; }
}

public sealed record ConnectorCatalogNameEntry(string Name, bool Enabled);

/// <summary>
/// Owns Host-default connector-name precedence across catalog readers and scope writes.
/// </summary>
public interface IConnectorCatalogNameAuthority
{
    IReadOnlyList<StoredConnectorDefinition> ComposeDefinitions(
        IReadOnlyList<StoredConnectorDefinition> scopedConnectors);

    IReadOnlyList<string> ComposeEnabledNames(
        IReadOnlyList<ConnectorCatalogNameEntry> scopedConnectors);

    IReadOnlyList<StoredConnectorDefinition> SelectScopeOwnedDefinitions(
        IReadOnlyList<StoredConnectorDefinition> requestedConnectors);
}
