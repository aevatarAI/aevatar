namespace Aevatar.Tools.Cli.Studio.Application.Abstractions;

public interface IConnectorCatalogStore
{
    Task<StoredConnectorCatalog> GetConnectorCatalogAsync(CancellationToken cancellationToken = default);

    Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(
        StoredConnectorCatalog catalog,
        CancellationToken cancellationToken = default);

    Task<ImportedConnectorCatalog> ImportLocalCatalogAsync(CancellationToken cancellationToken = default);

    Task<StoredConnectorDraft> GetConnectorDraftAsync(CancellationToken cancellationToken = default);

    Task<StoredConnectorDraft> SaveConnectorDraftAsync(
        StoredConnectorDraft draft,
        CancellationToken cancellationToken = default);

    Task DeleteConnectorDraftAsync(CancellationToken cancellationToken = default);
}

public sealed record ImportedConnectorCatalog(
    string SourceFilePath,
    bool SourceFileExists,
    StoredConnectorCatalog Catalog);
