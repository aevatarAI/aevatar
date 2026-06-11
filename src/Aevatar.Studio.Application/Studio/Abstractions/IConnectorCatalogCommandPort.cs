namespace Aevatar.Studio.Application.Studio.Abstractions;

// Refactor (iter56/cluster-911-studio-store-query-command):
//   old=Store mixed read/write + hand-built EventEnvelope
//   new=split query/command port + CQRS Core dispatch
public interface IConnectorCatalogCommandPort
{
    Task<ImportedConnectorCatalog> ImportLocalCatalogAsync(CancellationToken cancellationToken = default);

    Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(
        StoredConnectorCatalog catalog,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);

    Task<StoredConnectorDraft> SaveConnectorDraftAsync(
        StoredConnectorDraft draft,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);

    Task DeleteConnectorDraftAsync(
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);
}

public sealed record ImportedConnectorCatalog(
    string SourceFilePath,
    bool SourceFileExists,
    StoredConnectorCatalog Catalog);
