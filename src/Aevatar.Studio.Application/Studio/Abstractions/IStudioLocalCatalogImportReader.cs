namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IStudioLocalConnectorCatalogImportReader
{
    Task<StoredConnectorCatalog> ReadAsync(CancellationToken ct = default);
}

public interface IStudioLocalRoleCatalogImportReader
{
    Task<StoredRoleCatalog> ReadAsync(CancellationToken ct = default);
}
