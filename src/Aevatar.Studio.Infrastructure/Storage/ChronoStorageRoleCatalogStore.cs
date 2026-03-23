using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageRoleCatalogStore : IRoleCatalogStore
{
    private const string EncryptedCatalogFileName = "catalog.json.enc";

    private readonly IStudioWorkspaceStore _localWorkspaceStore;
    private readonly ChronoStorageCatalogBlobClient _blobClient;
    private readonly ConnectorCatalogStorageOptions _options;
    private readonly string _draftsDirectory;

    public ChronoStorageRoleCatalogStore(
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
        _draftsDirectory = Path.Combine(resolvedStudioOptions.RootDirectory, "roles-drafts");
        Directory.CreateDirectory(_draftsDirectory);
    }

    public async Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.RolesPrefix, EncryptedCatalogFileName);
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.GetRoleCatalogAsync(cancellationToken);
        }

        var downloadedPayload = await _blobClient.TryDownloadEncryptedAsync(remoteContext, cancellationToken);
        if (downloadedPayload is null)
        {
            return CreateRemoteCatalog(remoteContext, fileExists: false, []);
        }

        var plaintext = _blobClient.DecryptPayload(remoteContext, downloadedPayload.Payload, downloadedPayload.ObjectKey);
        await using var stream = new MemoryStream(plaintext, writable: false);
        var roles = await RoleCatalogJsonSerializer.ReadCatalogAsync(stream, cancellationToken);
        return CreateRemoteCatalog(remoteContext, fileExists: true, roles);
    }

    public async Task<StoredRoleCatalog> SaveRoleCatalogAsync(
        StoredRoleCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.RolesPrefix, EncryptedCatalogFileName);
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.SaveRoleCatalogAsync(catalog, cancellationToken);
        }

        await UploadCatalogAsync(remoteContext, catalog.Roles, cancellationToken);
        return CreateRemoteCatalog(remoteContext, fileExists: true, catalog.Roles);
    }

    public async Task<ImportedRoleCatalog> ImportLocalCatalogAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.RolesPrefix, EncryptedCatalogFileName)
                           ?? throw new InvalidOperationException("Chrono-storage role catalog import requires remote storage to be enabled.");
        var localCatalog = await _localWorkspaceStore.GetRoleCatalogAsync(cancellationToken);
        if (!localCatalog.FileExists)
        {
            throw new InvalidOperationException($"Local role catalog not found at '{localCatalog.FilePath}'.");
        }

        await UploadCatalogAsync(remoteContext, localCatalog.Roles, cancellationToken);
        var importedCatalog = CreateRemoteCatalog(remoteContext, fileExists: true, localCatalog.Roles);
        return new ImportedRoleCatalog(localCatalog.FilePath, true, importedCatalog);
    }

    public async Task<StoredRoleDraft> GetRoleDraftAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.RolesPrefix, EncryptedCatalogFileName);
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.GetRoleDraftAsync(cancellationToken);
        }

        var draftFilePath = GetDraftFilePath(remoteContext.OwnerKey);
        if (!File.Exists(draftFilePath))
        {
            return new StoredRoleDraft(
                HomeDirectory: _draftsDirectory,
                FilePath: draftFilePath,
                FileExists: false,
                UpdatedAtUtc: null,
                Draft: null);
        }

        await using var stream = File.OpenRead(draftFilePath);
        var parsed = await RoleCatalogJsonSerializer.ReadDraftAsync(
            stream,
            fallbackUpdatedAtUtc: new DateTimeOffset(File.GetLastWriteTimeUtc(draftFilePath), TimeSpan.Zero),
            cancellationToken);

        return new StoredRoleDraft(
            HomeDirectory: _draftsDirectory,
            FilePath: draftFilePath,
            FileExists: true,
            UpdatedAtUtc: parsed.UpdatedAtUtc,
            Draft: parsed.Draft);
    }

    public async Task<StoredRoleDraft> SaveRoleDraftAsync(
        StoredRoleDraft draft,
        CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.RolesPrefix, EncryptedCatalogFileName);
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.SaveRoleDraftAsync(draft, cancellationToken);
        }

        var draftFilePath = GetDraftFilePath(remoteContext.OwnerKey);
        var updatedAtUtc = draft.UpdatedAtUtc ?? DateTimeOffset.UtcNow;
        await WriteDraftFileAsync(draftFilePath, draft.Draft, updatedAtUtc, cancellationToken);

        return new StoredRoleDraft(
            HomeDirectory: _draftsDirectory,
            FilePath: draftFilePath,
            FileExists: true,
            UpdatedAtUtc: updatedAtUtc,
            Draft: draft.Draft);
    }

    public Task DeleteRoleDraftAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.RolesPrefix, EncryptedCatalogFileName);
        if (remoteContext is null)
        {
            return _localWorkspaceStore.DeleteRoleDraftAsync(cancellationToken);
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
        IReadOnlyList<StoredRoleDefinition> roles,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        await RoleCatalogJsonSerializer.WriteCatalogAsync(stream, roles, cancellationToken);
        var encryptedPayload = _blobClient.EncryptPayload(remoteContext, stream.ToArray());
        await _blobClient.UploadEncryptedAsync(remoteContext, encryptedPayload, cancellationToken);
    }

    private async Task WriteDraftFileAsync(
        string filePath,
        StoredRoleDefinition? draft,
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
                await RoleCatalogJsonSerializer.WriteDraftAsync(stream, draft, updatedAtUtc, cancellationToken);
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

    private StoredRoleCatalog CreateRemoteCatalog(
        ChronoStorageCatalogBlobClient.RemoteScopeContext remoteContext,
        bool fileExists,
        IReadOnlyList<StoredRoleDefinition> roles) =>
        new(
            HomeDirectory: _blobClient.CreateRemoteHomeDirectory(remoteContext),
            FilePath: _blobClient.CreateRemoteFilePath(remoteContext),
            FileExists: fileExists,
            Roles: roles);
}
