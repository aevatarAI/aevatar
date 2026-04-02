using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageRoleCatalogStore : IRoleCatalogStore
{
    private const string CatalogFileName = "roles.json";
    private const string DraftFileName = "roles.draft.json";

    private readonly IStudioWorkspaceStore _localWorkspaceStore;
    private readonly ChronoStorageCatalogBlobClient _blobClient;
    private readonly ConnectorCatalogStorageOptions _options;

    public ChronoStorageRoleCatalogStore(
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

    public async Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.RolesPrefix, CatalogFileName);
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.GetRoleCatalogAsync(cancellationToken);
        }

        var payload = await _blobClient.TryDownloadAsync(remoteContext, cancellationToken);
        if (payload is null)
        {
            return CreateRemoteCatalog(remoteContext, fileExists: false, []);
        }

        await using var stream = new MemoryStream(payload, writable: false);
        var roles = await RoleCatalogJsonSerializer.ReadCatalogAsync(stream, cancellationToken);
        return CreateRemoteCatalog(remoteContext, fileExists: true, roles);
    }

    public async Task<StoredRoleCatalog> SaveRoleCatalogAsync(
        StoredRoleCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.RolesPrefix, CatalogFileName);
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.SaveRoleCatalogAsync(catalog, cancellationToken);
        }

        await UploadCatalogAsync(remoteContext, catalog.Roles, cancellationToken);
        return CreateRemoteCatalog(remoteContext, fileExists: true, catalog.Roles);
    }

    public async Task<ImportedRoleCatalog> ImportLocalCatalogAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.RolesPrefix, CatalogFileName)
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
        var remoteContext = _blobClient.TryResolveContext(_options.RolesPrefix, DraftFileName);
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.GetRoleDraftAsync(cancellationToken);
        }

        var payload = await _blobClient.TryDownloadAsync(remoteContext, cancellationToken);
        if (payload is null)
        {
            return new StoredRoleDraft(
                HomeDirectory: _blobClient.CreateRemoteHomeDirectory(remoteContext),
                FilePath: _blobClient.CreateRemoteFilePath(remoteContext),
                FileExists: false,
                UpdatedAtUtc: null,
                Draft: null);
        }

        await using var stream = new MemoryStream(payload, writable: false);
        var parsed = await RoleCatalogJsonSerializer.ReadDraftAsync(
            stream,
            fallbackUpdatedAtUtc: DateTimeOffset.UtcNow,
            cancellationToken);

        return new StoredRoleDraft(
            HomeDirectory: _blobClient.CreateRemoteHomeDirectory(remoteContext),
            FilePath: _blobClient.CreateRemoteFilePath(remoteContext),
            FileExists: true,
            UpdatedAtUtc: parsed.UpdatedAtUtc,
            Draft: parsed.Draft);
    }

    public async Task<StoredRoleDraft> SaveRoleDraftAsync(
        StoredRoleDraft draft,
        CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.RolesPrefix, DraftFileName);
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.SaveRoleDraftAsync(draft, cancellationToken);
        }

        var updatedAtUtc = draft.UpdatedAtUtc ?? DateTimeOffset.UtcNow;
        await UploadDraftAsync(remoteContext, draft.Draft, updatedAtUtc, cancellationToken);

        return new StoredRoleDraft(
            HomeDirectory: _blobClient.CreateRemoteHomeDirectory(remoteContext),
            FilePath: _blobClient.CreateRemoteFilePath(remoteContext),
            FileExists: true,
            UpdatedAtUtc: updatedAtUtc,
            Draft: draft.Draft);
    }

    public Task DeleteRoleDraftAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.RolesPrefix, DraftFileName);
        if (remoteContext is null)
        {
            return _localWorkspaceStore.DeleteRoleDraftAsync(cancellationToken);
        }

        return _blobClient.DeleteIfExistsAsync(remoteContext, cancellationToken);
    }

    private async Task UploadCatalogAsync(
        ChronoStorageCatalogBlobClient.RemoteScopeContext remoteContext,
        IReadOnlyList<StoredRoleDefinition> roles,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        await RoleCatalogJsonSerializer.WriteCatalogAsync(stream, roles, cancellationToken);
        await _blobClient.UploadAsync(remoteContext, stream.ToArray(), "application/json", cancellationToken);
    }

    private async Task UploadDraftAsync(
        ChronoStorageCatalogBlobClient.RemoteScopeContext remoteContext,
        StoredRoleDefinition? draft,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        await RoleCatalogJsonSerializer.WriteDraftAsync(stream, draft, updatedAtUtc, cancellationToken);
        await _blobClient.UploadAsync(remoteContext, stream.ToArray(), "application/json", cancellationToken);
    }

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
