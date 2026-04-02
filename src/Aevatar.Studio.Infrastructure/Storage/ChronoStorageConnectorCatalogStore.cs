using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageConnectorCatalogStore : IConnectorCatalogStore
{
    private const string CatalogFileName = "connectors.json";
    private const string DraftFileName = "connectors.draft.json";

    private readonly IStudioWorkspaceStore _localWorkspaceStore;
    private readonly ChronoStorageCatalogBlobClient _blobClient;
    private readonly ConnectorCatalogStorageOptions _options;

    public ChronoStorageConnectorCatalogStore(
        IStudioWorkspaceStore localWorkspaceStore,
        ChronoStorageCatalogBlobClient blobClient,
        IOptions<ConnectorCatalogStorageOptions> options,
        IOptions<StudioStorageOptions> studioStorageOptions)
    {
        _localWorkspaceStore = localWorkspaceStore ?? throw new ArgumentNullException(nameof(localWorkspaceStore));
        _blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _ = studioStorageOptions?.Value.ResolveRootDirectory()
            ?? throw new ArgumentNullException(nameof(studioStorageOptions));
    }

    public async Task<StoredConnectorCatalog> GetConnectorCatalogAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.Prefix, CatalogFileName);
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.GetConnectorCatalogAsync(cancellationToken);
        }

        var payload = await _blobClient.TryDownloadAsync(remoteContext, cancellationToken);
        if (payload is null)
        {
            return CreateRemoteCatalog(remoteContext, fileExists: false, []);
        }

        await using var stream = new MemoryStream(payload, writable: false);
        var connectors = await ConnectorCatalogJsonSerializer.ReadCatalogAsync(stream, cancellationToken);
        return CreateRemoteCatalog(remoteContext, fileExists: true, connectors);
    }

    public async Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(
        StoredConnectorCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.Prefix, CatalogFileName);
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.SaveConnectorCatalogAsync(catalog, cancellationToken);
        }

        await UploadCatalogAsync(remoteContext, catalog.Connectors, cancellationToken);
        return CreateRemoteCatalog(remoteContext, fileExists: true, catalog.Connectors);
    }

    public async Task<ImportedConnectorCatalog> ImportLocalCatalogAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.Prefix, CatalogFileName)
                           ?? throw new InvalidOperationException("Chrono-storage connector catalog import requires remote storage to be enabled.");
        var localCatalog = await _localWorkspaceStore.GetConnectorCatalogAsync(cancellationToken);
        if (!localCatalog.FileExists)
        {
            throw new InvalidOperationException($"Local connector catalog not found at '{localCatalog.FilePath}'.");
        }

        await UploadCatalogAsync(remoteContext, localCatalog.Connectors, cancellationToken);
        var importedCatalog = CreateRemoteCatalog(remoteContext, fileExists: true, localCatalog.Connectors);
        return new ImportedConnectorCatalog(localCatalog.FilePath, true, importedCatalog);
    }

    public async Task<StoredConnectorDraft> GetConnectorDraftAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.Prefix, DraftFileName);
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.GetConnectorDraftAsync(cancellationToken);
        }

        var payload = await _blobClient.TryDownloadAsync(remoteContext, cancellationToken);
        if (payload is null)
        {
            return new StoredConnectorDraft(
                HomeDirectory: _blobClient.CreateRemoteHomeDirectory(remoteContext),
                FilePath: _blobClient.CreateRemoteFilePath(remoteContext),
                FileExists: false,
                UpdatedAtUtc: null,
                Draft: null);
        }

        await using var stream = new MemoryStream(payload, writable: false);
        var parsed = await ConnectorCatalogJsonSerializer.ReadDraftAsync(
            stream,
            fallbackUpdatedAtUtc: DateTimeOffset.UtcNow,
            cancellationToken);

        return new StoredConnectorDraft(
            HomeDirectory: _blobClient.CreateRemoteHomeDirectory(remoteContext),
            FilePath: _blobClient.CreateRemoteFilePath(remoteContext),
            FileExists: true,
            UpdatedAtUtc: parsed.UpdatedAtUtc,
            Draft: parsed.Draft);
    }

    public async Task<StoredConnectorDraft> SaveConnectorDraftAsync(
        StoredConnectorDraft draft,
        CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.Prefix, DraftFileName);
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.SaveConnectorDraftAsync(draft, cancellationToken);
        }

        var updatedAtUtc = draft.UpdatedAtUtc ?? DateTimeOffset.UtcNow;
        await UploadDraftAsync(remoteContext, draft.Draft, updatedAtUtc, cancellationToken);

        return new StoredConnectorDraft(
            HomeDirectory: _blobClient.CreateRemoteHomeDirectory(remoteContext),
            FilePath: _blobClient.CreateRemoteFilePath(remoteContext),
            FileExists: true,
            UpdatedAtUtc: updatedAtUtc,
            Draft: draft.Draft);
    }

    public Task DeleteConnectorDraftAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.Prefix, DraftFileName);
        if (remoteContext is null)
        {
            return _localWorkspaceStore.DeleteConnectorDraftAsync(cancellationToken);
        }

        return _blobClient.DeleteIfExistsAsync(remoteContext, cancellationToken);
    }

    private async Task UploadCatalogAsync(
        ChronoStorageCatalogBlobClient.RemoteScopeContext remoteContext,
        IReadOnlyList<StoredConnectorDefinition> connectors,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        await ConnectorCatalogJsonSerializer.WriteCatalogAsync(stream, connectors, cancellationToken);
        await _blobClient.UploadAsync(remoteContext, stream.ToArray(), "application/json", cancellationToken);
    }

    private async Task UploadDraftAsync(
        ChronoStorageCatalogBlobClient.RemoteScopeContext remoteContext,
        StoredConnectorDefinition? draft,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        await ConnectorCatalogJsonSerializer.WriteDraftAsync(stream, draft, updatedAtUtc, cancellationToken);
        await _blobClient.UploadAsync(remoteContext, stream.ToArray(), "application/json", cancellationToken);
    }

    private StoredConnectorCatalog CreateRemoteCatalog(
        ChronoStorageCatalogBlobClient.RemoteScopeContext remoteContext,
        bool fileExists,
        IReadOnlyList<StoredConnectorDefinition> connectors) =>
        new(
            HomeDirectory: _blobClient.CreateRemoteHomeDirectory(remoteContext),
            FilePath: _blobClient.CreateRemoteFilePath(remoteContext),
            FileExists: fileExists,
            Connectors: connectors);
}
