using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Tools.Cli.Hosting;
using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageConnectorCatalogStore : IConnectorCatalogStore
{
    private const string EncryptedCatalogFileName = "catalog.json.enc";

    private readonly IStudioWorkspaceStore _localWorkspaceStore;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConnectorCatalogStorageOptions _options;
    private readonly string _draftsDirectory;
    private readonly ILogger<ChronoStorageConnectorCatalogStore> _logger;

    public ChronoStorageConnectorCatalogStore(
        IStudioWorkspaceStore localWorkspaceStore,
        IAppScopeResolver scopeResolver,
        IHttpClientFactory httpClientFactory,
        IOptions<ConnectorCatalogStorageOptions> options,
        IOptions<StudioStorageOptions> studioStorageOptions,
        ILogger<ChronoStorageConnectorCatalogStore> logger)
    {
        _localWorkspaceStore = localWorkspaceStore ?? throw new ArgumentNullException(nameof(localWorkspaceStore));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var resolvedStudioOptions = studioStorageOptions?.Value.ResolveRootDirectory()
                                   ?? throw new ArgumentNullException(nameof(studioStorageOptions));
        _draftsDirectory = Path.Combine(resolvedStudioOptions.RootDirectory, "connectors-drafts");
        Directory.CreateDirectory(_draftsDirectory);
    }

    public async Task<StoredConnectorCatalog> GetConnectorCatalogAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = TryResolveRemoteContext();
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.GetConnectorCatalogAsync(cancellationToken);
        }

        var payload = await TryDownloadCatalogAsync(remoteContext, cancellationToken);
        if (payload is not null)
        {
            var connectors = await DecryptCatalogAsync(remoteContext, payload, cancellationToken);
            return CreateRemoteCatalog(remoteContext, fileExists: true, connectors);
        }

        var localCatalog = await _localWorkspaceStore.GetConnectorCatalogAsync(cancellationToken);
        if (localCatalog.FileExists && localCatalog.Connectors.Count > 0)
        {
            await UploadCatalogAsync(remoteContext, localCatalog.Connectors, cancellationToken);
            _logger.LogInformation(
                "Imported local connectors.json into chrono-storage for scope {ScopeId}.",
                remoteContext.Scope.ScopeId);
            return CreateRemoteCatalog(remoteContext, fileExists: true, localCatalog.Connectors);
        }

        return CreateRemoteCatalog(remoteContext, fileExists: false, []);
    }

    public async Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(
        StoredConnectorCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        var remoteContext = TryResolveRemoteContext();
        if (remoteContext is null)
        {
            return await _localWorkspaceStore.SaveConnectorCatalogAsync(catalog, cancellationToken);
        }

        await UploadCatalogAsync(remoteContext, catalog.Connectors, cancellationToken);
        return CreateRemoteCatalog(remoteContext, fileExists: true, catalog.Connectors);
    }

    public async Task<StoredConnectorDraft> GetConnectorDraftAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = TryResolveRemoteContext();
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
        var remoteContext = TryResolveRemoteContext();
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
        var remoteContext = TryResolveRemoteContext();
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

    private RemoteScopeContext? TryResolveRemoteContext()
    {
        if (!_options.Enabled)
        {
            return null;
        }

        var baseUrl = _options.BaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("Cli:App:Connectors:ChronoStorage:BaseUrl must be a valid absolute URL.");
        }

        var bucket = _options.Bucket?.Trim();
        if (string.IsNullOrWhiteSpace(bucket))
        {
            throw new InvalidOperationException("Cli:App:Connectors:ChronoStorage:Bucket is required when chrono-storage is enabled.");
        }

        var masterKey = _options.MasterKey?.Trim();
        if (string.IsNullOrWhiteSpace(masterKey))
        {
            throw new InvalidOperationException("Cli:App:Connectors:ChronoStorage:MasterKey is required when chrono-storage is enabled.");
        }

        var scope = _scopeResolver.Resolve();
        if (scope is null)
        {
            throw new InvalidOperationException("Chrono-storage connector catalog requires an authenticated or configured app scope.");
        }

        var prefix = NormalizePrefix(_options.Prefix);
        var ownerKey = CreateOwnerKey(scope.ScopeId, masterKey);
        var objectKey = string.IsNullOrWhiteSpace(prefix)
            ? $"{ownerKey}/{EncryptedCatalogFileName}"
            : $"{prefix}/{ownerKey}/{EncryptedCatalogFileName}";

        return new RemoteScopeContext(
            Scope: scope,
            BaseUri: new Uri(baseUri.ToString().TrimEnd('/')),
            Bucket: bucket,
            Prefix: prefix,
            MasterKey: masterKey,
            OwnerKey: ownerKey,
            ObjectKey: objectKey,
            PresignedUrlExpiresInSeconds: Math.Clamp(_options.PresignedUrlExpiresInSeconds <= 0 ? 300 : _options.PresignedUrlExpiresInSeconds, 30, 3600),
            CreateBucketIfMissing: _options.CreateBucketIfMissing,
            StaticBearerToken: _options.StaticBearerToken?.Trim());
    }

    private async Task<byte[]?> TryDownloadCatalogAsync(
        RemoteScopeContext context,
        CancellationToken cancellationToken)
    {
        using var request = CreateChronoStorageRequest(
            HttpMethod.Get,
            CreateChronoStorageUri(
                context.BaseUri,
                $"api/buckets/{Uri.EscapeDataString(context.Bucket)}/presigned-url",
                $"key={Uri.EscapeDataString(context.ObjectKey)}&expiresIn={context.PresignedUrlExpiresInSeconds}"),
            context.StaticBearerToken);
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<ChronoStorageEnvelope<PresignedUrlPayload>>(cancellationToken);
        var downloadUrl = envelope?.Data?.Url?.Trim();
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new InvalidOperationException("Chrono-storage presigned URL response did not include a usable download URL.");
        }

        using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        using var downloadResponse = await _httpClientFactory.CreateClient().SendAsync(downloadRequest, cancellationToken);
        if (downloadResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        downloadResponse.EnsureSuccessStatusCode();
        return await downloadResponse.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task UploadCatalogAsync(
        RemoteScopeContext context,
        IReadOnlyList<StoredConnectorDefinition> connectors,
        CancellationToken cancellationToken)
    {
        if (context.CreateBucketIfMissing)
        {
            await EnsureBucketExistsAsync(context, cancellationToken);
        }

        var payload = await EncryptCatalogAsync(context, connectors, cancellationToken);
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var request = CreateChronoStorageRequest(
            HttpMethod.Post,
            CreateChronoStorageUri(
                context.BaseUri,
                $"api/buckets/{Uri.EscapeDataString(context.Bucket)}/objects",
                $"key={Uri.EscapeDataString(context.ObjectKey)}&contentType={Uri.EscapeDataString("application/octet-stream")}"),
            context.StaticBearerToken,
            content);
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task EnsureBucketExistsAsync(RemoteScopeContext context, CancellationToken cancellationToken)
    {
        using var headRequest = CreateChronoStorageRequest(
            HttpMethod.Head,
            CreateChronoStorageUri(context.BaseUri, $"api/buckets/{Uri.EscapeDataString(context.Bucket)}"),
            context.StaticBearerToken);
        using var headResponse = await _httpClientFactory.CreateClient().SendAsync(headRequest, cancellationToken);
        if (headResponse.StatusCode != HttpStatusCode.NotFound)
        {
            headResponse.EnsureSuccessStatusCode();
            return;
        }

        using var createRequest = CreateChronoStorageRequest(
            HttpMethod.Post,
            CreateChronoStorageUri(context.BaseUri, "api/buckets"),
            context.StaticBearerToken,
            JsonContent.Create(new { name = context.Bucket }));
        using var createResponse = await _httpClientFactory.CreateClient().SendAsync(createRequest, cancellationToken);
        if (createResponse.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        createResponse.EnsureSuccessStatusCode();
    }

    private async Task<byte[]> EncryptCatalogAsync(
        RemoteScopeContext context,
        IReadOnlyList<StoredConnectorDefinition> connectors,
        CancellationToken cancellationToken)
    {
        await using var plainStream = new MemoryStream();
        await ConnectorCatalogJsonSerializer.WriteCatalogAsync(plainStream, connectors, cancellationToken);
        var plaintext = plainStream.ToArray();

        var encryptionKey = DeriveKeyMaterial(context.MasterKey, "connector-catalog-encryption");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        var associatedData = Encoding.UTF8.GetBytes(GetAssociatedData(context));
        using var aesGcm = new AesGcm(encryptionKey, tagSizeInBytes: tag.Length);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        return JsonSerializer.SerializeToUtf8Bytes(new EncryptedConnectorCatalogEnvelope
        {
            Version = 1,
            Nonce = Convert.ToBase64String(nonce),
            Ciphertext = Convert.ToBase64String(ciphertext),
            Tag = Convert.ToBase64String(tag),
        });
    }

    private async Task<IReadOnlyList<StoredConnectorDefinition>> DecryptCatalogAsync(
        RemoteScopeContext context,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var envelope = JsonSerializer.Deserialize<EncryptedConnectorCatalogEnvelope>(payload)
                       ?? throw new InvalidOperationException("Connector catalog payload is empty.");
        if (envelope.Version != 1)
        {
            throw new InvalidOperationException($"Unsupported connector catalog payload version '{envelope.Version}'.");
        }

        var nonce = Convert.FromBase64String(envelope.Nonce);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var tag = Convert.FromBase64String(envelope.Tag);
        var plaintext = new byte[ciphertext.Length];
        var encryptionKey = DeriveKeyMaterial(context.MasterKey, "connector-catalog-encryption");
        var associatedData = Encoding.UTF8.GetBytes(GetAssociatedData(context));
        using var aesGcm = new AesGcm(encryptionKey, tagSizeInBytes: tag.Length);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

        await using var stream = new MemoryStream(plaintext, writable: false);
        return await ConnectorCatalogJsonSerializer.ReadCatalogAsync(stream, cancellationToken);
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

    private static HttpRequestMessage CreateChronoStorageRequest(
        HttpMethod method,
        Uri uri,
        string? staticBearerToken,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = content,
        };

        if (!string.IsNullOrWhiteSpace(staticBearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staticBearerToken);
        }

        return request;
    }

    private static Uri CreateChronoStorageUri(Uri baseUri, string relativePath, string? query = null)
    {
        var builder = new UriBuilder(new Uri(baseUri, relativePath))
        {
            Query = query ?? string.Empty,
        };
        return builder.Uri;
    }

    private static string NormalizePrefix(string? prefix) =>
        string.Join(
            '/',
            (prefix ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string CreateOwnerKey(string scopeId, string masterKey)
    {
        var ownerKeyBytes = HMACSHA256.HashData(
            DeriveKeyMaterial(masterKey, "connector-catalog-owner"),
            Encoding.UTF8.GetBytes(scopeId));
        return Convert.ToHexString(ownerKeyBytes).ToLowerInvariant();
    }

    private static byte[] DeriveKeyMaterial(string masterKey, string purpose)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(masterKey));
        return HMACSHA256.HashData(seed, Encoding.UTF8.GetBytes(purpose));
    }

    private static string GetAssociatedData(RemoteScopeContext context) =>
        $"{context.Bucket}|{context.ObjectKey}|v1";

    private static StoredConnectorCatalog CreateRemoteCatalog(
        RemoteScopeContext context,
        bool fileExists,
        IReadOnlyList<StoredConnectorDefinition> connectors) =>
        new(
            HomeDirectory: CreateRemoteHomeDirectory(context),
            FilePath: $"chrono-storage://{context.Bucket}/{context.ObjectKey}",
            FileExists: fileExists,
            Connectors: connectors);

    private static string CreateRemoteHomeDirectory(RemoteScopeContext context) =>
        string.IsNullOrWhiteSpace(context.Prefix)
            ? $"chrono-storage://{context.Bucket}/{context.OwnerKey}"
            : $"chrono-storage://{context.Bucket}/{context.Prefix}/{context.OwnerKey}";

    private sealed record RemoteScopeContext(
        AppScopeContext Scope,
        Uri BaseUri,
        string Bucket,
        string Prefix,
        string MasterKey,
        string OwnerKey,
        string ObjectKey,
        int PresignedUrlExpiresInSeconds,
        bool CreateBucketIfMissing,
        string? StaticBearerToken);

    private sealed class ChronoStorageEnvelope<T>
    {
        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }

    private sealed class PresignedUrlPayload
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
    }

    private sealed class EncryptedConnectorCatalogEnvelope
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("nonce")]
        public string Nonce { get; set; } = string.Empty;

        [JsonPropertyName("ciphertext")]
        public string Ciphertext { get; set; } = string.Empty;

        [JsonPropertyName("tag")]
        public string Tag { get; set; } = string.Empty;
    }
}
