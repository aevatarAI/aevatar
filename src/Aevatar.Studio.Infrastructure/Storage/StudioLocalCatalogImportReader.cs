using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class StudioLocalCatalogImportReader :
    IStudioLocalConnectorCatalogImportReader,
    IStudioLocalRoleCatalogImportReader
{
    private readonly StudioStorageOptions _options;

    public StudioLocalCatalogImportReader(IOptions<StudioStorageOptions> options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value.ResolveRootDirectory();
    }

    public async Task<StoredConnectorCatalog> ReadAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(_options.RootDirectory, "connectors.json");
        if (!File.Exists(path))
        {
            return new StoredConnectorCatalog(
                HomeDirectory: _options.RootDirectory,
                FilePath: path,
                FileExists: false,
                Connectors: []);
        }

        await using var stream = File.OpenRead(path);
        var connectors = await ConnectorCatalogStorageSerializer.ReadCatalogAsync(stream, ct);
        return new StoredConnectorCatalog(
            HomeDirectory: _options.RootDirectory,
            FilePath: path,
            FileExists: true,
            Connectors: connectors);
    }

    async Task<StoredRoleCatalog> IStudioLocalRoleCatalogImportReader.ReadAsync(CancellationToken ct)
    {
        var path = Path.Combine(_options.RootDirectory, "roles.json");
        if (!File.Exists(path))
        {
            return new StoredRoleCatalog(
                HomeDirectory: _options.RootDirectory,
                FilePath: path,
                FileExists: false,
                Roles: []);
        }

        await using var stream = File.OpenRead(path);
        var roles = await RoleCatalogStorageSerializer.ReadCatalogAsync(stream, ct);
        return new StoredRoleCatalog(
            HomeDirectory: _options.RootDirectory,
            FilePath: path,
            FileExists: true,
            Roles: roles);
    }
}
