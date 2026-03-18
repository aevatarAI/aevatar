namespace Aevatar.Tools.Cli.Studio.Application.Abstractions;

public interface IRoleCatalogStore
{
    Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default);

    Task<StoredRoleCatalog> SaveRoleCatalogAsync(
        StoredRoleCatalog catalog,
        CancellationToken cancellationToken = default);

    Task<ImportedRoleCatalog> ImportLocalCatalogAsync(CancellationToken cancellationToken = default);

    Task<StoredRoleDraft> GetRoleDraftAsync(CancellationToken cancellationToken = default);

    Task<StoredRoleDraft> SaveRoleDraftAsync(
        StoredRoleDraft draft,
        CancellationToken cancellationToken = default);

    Task DeleteRoleDraftAsync(CancellationToken cancellationToken = default);
}

public sealed record ImportedRoleCatalog(
    string SourceFilePath,
    bool SourceFileExists,
    StoredRoleCatalog Catalog);
