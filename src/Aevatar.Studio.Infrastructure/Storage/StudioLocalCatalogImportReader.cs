using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class StudioLocalCatalogImportReader :
    IStudioLocalConnectorCatalogImportReader,
    IStudioLocalRoleCatalogImportReader
{
    private readonly StudioStorageOptions _options;
    private readonly IConnectorCatalogImportParser _connectorImportParser;
    private readonly IRoleCatalogImportParser _roleImportParser;

    public StudioLocalCatalogImportReader(
        IOptions<StudioStorageOptions> options,
        IConnectorCatalogImportParser connectorImportParser,
        IRoleCatalogImportParser roleImportParser)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value.ResolveRootDirectory();
        _connectorImportParser = connectorImportParser ?? throw new ArgumentNullException(nameof(connectorImportParser));
        _roleImportParser = roleImportParser ?? throw new ArgumentNullException(nameof(roleImportParser));
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
        var connectors = await _connectorImportParser.ParseCatalogAsync(stream, ct);
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
        var roles = await _roleImportParser.ParseCatalogAsync(stream, ct);
        return new StoredRoleCatalog(
            HomeDirectory: _options.RootDirectory,
            FilePath: path,
            FileExists: true,
            Roles: roles);
    }
}
