namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IRoleCatalogStore
{
    Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default);

    Task<StoredRoleCatalog> SaveRoleCatalogAsync(
        StoredRoleCatalog catalog,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);

    Task<ImportedRoleCatalog> ImportLocalCatalogAsync(CancellationToken cancellationToken = default);

    Task<StoredRoleDraft> GetRoleDraftAsync(CancellationToken cancellationToken = default);

    Task<StoredRoleDraft> SaveRoleDraftAsync(
        StoredRoleDraft draft,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);

    Task DeleteRoleDraftAsync(
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);
}

public sealed record ImportedRoleCatalog(
    string SourceFilePath,
    bool SourceFileExists,
    StoredRoleCatalog Catalog);
