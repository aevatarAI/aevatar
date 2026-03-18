using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageConnectorCatalogStore : IConnectorCatalogStore
{
    private const string EncryptedCatalogFileName = "catalog.json.enc";

    private readonly IStudioWorkspaceStore _localWorkspaceStore;
    private readonly ChronoStorageCatalogBlobClient _blobClient;
    private readonly ConnectorCatalogStorageOptions _options;
    private readonly string _draftsDirectory;

    public ChronoStorageConnectorCatalogStore(
        IStudioWorkspaceStore localWorkspaceStore,
        ChronoStorageCatalogBlobClient blobClient,
        IOptions<ConnectorCatalogStorageOptions> options,
        IOptions<StudioStorageOptions> studioStorageOptions)
    {
        _localWorkspaceStore = localWorkspaceStore ?? throw new ArgumentNullException(nameof(localWorkspaceStore));
        _blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        var resolvedStudioOptions = studioStorageOptions?.Value.ResolveRootDirectory()
                                   ?? throw new ArgumentNullException(nameof(studioStorageOptions));
        _draftsDirectory = Path.Combine(resolvedStudioOptions.RootDirectory, "connectors-drafts");
        Directory.CreateDirectory(_draftsDirectory);
    }

    public async Task<StoredConnectorCatalog> GetConnectorCatalogAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.Prefix, EncryptedCatalogFileName);
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.GetConnectorCatalogAsync(cancellationToken);
        }

        var encryptedPayload = await _blobClient.TryDownloadEncryptedAsync(remoteContext, cancellationToken);
        if (encryptedPayload is null)
        {
            return CreateRemoteCatalog(remoteContext, fileExists: false, []);
        }

        var plaintext = _blobClient.DecryptPayload(remoteContext, encryptedPayload);
        await using var stream = new MemoryStream(plaintext, writable: false);
        var connectors = await ConnectorCatalogJsonSerializer.ReadCatalogAsync(stream, cancellationToken);
        return CreateRemoteCatalog(remoteContext, fileExists: true, connectors);
    }

    public async Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(
        StoredConnectorCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.Prefix, EncryptedCatalogFileName);
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.SaveConnectorCatalogAsync(catalog, cancellationToken);
        }

        await UploadCatalogAsync(remoteContext, catalog.Connectors, cancellationToken);
        return CreateRemoteCatalog(remoteContext, fileExists: true, catalog.Connectors);
    }

    public async Task<ImportedConnectorCatalog> ImportLocalCatalogAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.Prefix, EncryptedCatalogFileName)
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
        var remoteContext = _blobClient.TryResolveContext(_options.Prefix, EncryptedCatalogFileName);
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.GetConnectorDraftAsync(cancellationToken);
        }

        var draftFilePath = GetDraftFilePath(remoteContext.OwnerKey);
        if (!File.Exists(draftFilePath))
        {
            return new StoredConnectorDraft(
                HomeDirectory: _draftsDirectory,
                FilePath: draftFilePath,
                FileExists: false,
                UpdatedAtUtc: null,
                Draft: null);
        }

        await using var stream = File.OpenRead(draftFilePath);
        var parsed = await ConnectorCatalogJsonSerializer.ReadDraftAsync(
            stream,
            fallbackUpdatedAtUtc: new DateTimeOffset(File.GetLastWriteTimeUtc(draftFilePath), TimeSpan.Zero),
            cancellationToken);

        return new StoredConnectorDraft(
            HomeDirectory: _draftsDirectory,
            FilePath: draftFilePath,
            FileExists: true,
            UpdatedAtUtc: parsed.UpdatedAtUtc,
            Draft: parsed.Draft);
    }

    public async Task<StoredConnectorDraft> SaveConnectorDraftAsync(
        StoredConnectorDraft draft,
        CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.Prefix, EncryptedCatalogFileName);
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.SaveConnectorDraftAsync(draft, cancellationToken);
        }

        var draftFilePath = GetDraftFilePath(remoteContext.OwnerKey);
        var updatedAtUtc = draft.UpdatedAtUtc ?? DateTimeOffset.UtcNow;
        await WriteDraftFileAsync(draftFilePath, draft.Draft, updatedAtUtc, cancellationToken);

        return new StoredConnectorDraft(
            HomeDirectory: _draftsDirectory,
            FilePath: draftFilePath,
            FileExists: true,
            UpdatedAtUtc: updatedAtUtc,
            Draft: draft.Draft);
    }

    public Task DeleteConnectorDraftAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.Prefix, EncryptedCatalogFileName);
        if (remoteContext is null)
        {
            return _localWorkspaceStore.DeleteConnectorDraftAsync(cancellationToken);
        }

        var draftFilePath = GetDraftFilePath(remoteContext.OwnerKey);
        if (File.Exists(draftFilePath))
        {
            File.Delete(draftFilePath);
        }

        return Task.CompletedTask;
    }

    private async Task UploadCatalogAsync(
        ChronoStorageCatalogBlobClient.RemoteScopeContext remoteContext,
        IReadOnlyList<StoredConnectorDefinition> connectors,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        await ConnectorCatalogJsonSerializer.WriteCatalogAsync(stream, connectors, cancellationToken);
        var encryptedPayload = _blobClient.EncryptPayload(remoteContext, stream.ToArray());
        await _blobClient.UploadEncryptedAsync(remoteContext, encryptedPayload, cancellationToken);
    }

    private async Task WriteDraftFileAsync(
        string filePath,
        StoredConnectorDefinition? draft,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var tempFilePath = Path.Combine(
            Path.GetDirectoryName(filePath)!,
            $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             tempFilePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await ConnectorCatalogJsonSerializer.WriteDraftAsync(stream, draft, updatedAtUtc, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(filePath))
            {
                File.Move(tempFilePath, filePath, overwrite: true);
            }
            else
            {
                File.Move(tempFilePath, filePath);
            }
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
    }

    private string GetDraftFilePath(string ownerKey) => Path.Combine(_draftsDirectory, $"{ownerKey}.json");

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
