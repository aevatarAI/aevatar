using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Tools.Cli.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageCatalogBlobClient
{
    public const string NyxProxyHttpClientName = "NyxProxyAuthorized";
    private const int MaxDownloadUrlAttempts = 2;

    private readonly IAppScopeResolver _scopeResolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConnectorCatalogStorageOptions _options;
    private readonly ChronoStorageMasterKeyResolver _masterKeyResolver;
    private readonly NyxIdAppAuthOptions? _nyxIdAppAuthOptions;

    public ChronoStorageCatalogBlobClient(
        IAppScopeResolver scopeResolver,
        IHttpClientFactory httpClientFactory,
        IOptions<ConnectorCatalogStorageOptions> options,
        ChronoStorageMasterKeyResolver masterKeyResolver,
        IOptions<NyxIdAppAuthOptions>? nyxIdAppAuthOptions = null)
    {
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _masterKeyResolver = masterKeyResolver ?? throw new ArgumentNullException(nameof(masterKeyResolver));
        _nyxIdAppAuthOptions = nyxIdAppAuthOptions?.Value;
    }

    public RemoteScopeContext? TryResolveContext(string prefix, string fileName)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        var bucket = _options.Bucket?.Trim();
        if (string.IsNullOrWhiteSpace(bucket))
        {
            throw new InvalidOperationException("Cli:App:Connectors:ChronoStorage:Bucket is required when chrono-storage is enabled.");
        }

        var endpoint = ResolveApiEndpoint();
        var masterKey = _masterKeyResolver.ResolveMasterKey(_options.MasterKey);

        var scope = _scopeResolver.Resolve();
        if (scope is null)
        {
            throw new InvalidOperationException("Chrono-storage catalog access requires an authenticated or configured app scope.");
        }

        var normalizedPrefix = NormalizePrefix(prefix);
        var ownerKey = CreateOwnerKey(scope.ScopeId);
        var objectKey = string.IsNullOrWhiteSpace(normalizedPrefix)
            ? $"{ownerKey}/{fileName}"
            : $"{normalizedPrefix}/{ownerKey}/{fileName}";
        var legacyOwnerKey = CreateLegacyOwnerKey(scope.ScopeId, masterKey);
        IReadOnlyList<string> legacyObjectKeys = string.Equals(legacyOwnerKey, ownerKey, StringComparison.Ordinal)
            ? Array.Empty<string>()
            : [
                string.IsNullOrWhiteSpace(normalizedPrefix)
                    ? $"{legacyOwnerKey}/{fileName}"
                    : $"{normalizedPrefix}/{legacyOwnerKey}/{fileName}",
            ];

        return new RemoteScopeContext(
            Scope: scope,
            BaseUri: endpoint.BaseUri,
            ApiRoutePrefix: endpoint.ApiRoutePrefix,
            Bucket: bucket,
            Prefix: normalizedPrefix,
            MasterKey: masterKey,
            OwnerKey: ownerKey,
            ObjectKey: objectKey,
            LegacyObjectKeys: legacyObjectKeys,
            PresignedUrlExpiresInSeconds: Math.Clamp(_options.PresignedUrlExpiresInSeconds <= 0 ? 300 : _options.PresignedUrlExpiresInSeconds, 30, 3600),
            CreateBucketIfMissing: _options.CreateBucketIfMissing,
            StaticBearerToken: _options.StaticBearerToken?.Trim());
    }

    public async Task<DownloadedCatalogPayload?> TryDownloadEncryptedAsync(
        RemoteScopeContext context,
        CancellationToken cancellationToken)
    {
        CatalogDownloadUnavailableException? downloadUnavailable = null;
        foreach (var objectKey in EnumerateCandidateObjectKeys(context))
        {
            try
            {
                var payload = await TryDownloadEncryptedCoreAsync(context, objectKey, cancellationToken);
                if (payload != null)
                {
                    return new DownloadedCatalogPayload(payload, objectKey);
                }
            }
            catch (CatalogDownloadUnavailableException exception)
            {
                downloadUnavailable ??= exception;
            }
        }

        if (downloadUnavailable != null)
        {
            throw downloadUnavailable;
        }

        return null;
    }

    public async Task UploadEncryptedAsync(
        RemoteScopeContext context,
        byte[] encryptedPayload,
        CancellationToken cancellationToken)
    {
        if (context.CreateBucketIfMissing)
        {
            await EnsureBucketExistsAsync(context, cancellationToken);
        }

        using var content = new ByteArrayContent(encryptedPayload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var request = CreateChronoStorageRequest(
            HttpMethod.Post,
            CreateChronoStorageUri(
                context,
                $"api/buckets/{Uri.EscapeDataString(context.Bucket)}/objects",
                $"key={Uri.EscapeDataString(context.ObjectKey)}&contentType={Uri.EscapeDataString("application/octet-stream")}"),
            context.StaticBearerToken,
            content);
        using var response = await GetCatalogApiClient(context).SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public byte[] EncryptPayload(RemoteScopeContext context, byte[] plaintext, string? objectKey = null)
    {
        var encryptionKey = DeriveKeyMaterial(context.MasterKey, "catalog-encryption");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        var associatedData = Encoding.UTF8.GetBytes(GetAssociatedData(context, objectKey ?? context.ObjectKey));
        using var aesGcm = new AesGcm(encryptionKey, tagSizeInBytes: tag.Length);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        return JsonSerializer.SerializeToUtf8Bytes(new EncryptedCatalogEnvelope
        {
            Version = 1,
            Nonce = Convert.ToBase64String(nonce),
            Ciphertext = Convert.ToBase64String(ciphertext),
            Tag = Convert.ToBase64String(tag),
        });
    }

    public byte[] DecryptPayload(RemoteScopeContext context, byte[] payload, string? objectKey = null)
    {
        var envelope = JsonSerializer.Deserialize<EncryptedCatalogEnvelope>(payload)
                       ?? throw new InvalidOperationException("Catalog payload is empty.");
        if (envelope.Version != 1)
        {
            throw new InvalidOperationException($"Unsupported catalog payload version '{envelope.Version}'.");
        }

        var nonce = Convert.FromBase64String(envelope.Nonce);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var tag = Convert.FromBase64String(envelope.Tag);
        var plaintext = new byte[ciphertext.Length];
        var encryptionKey = DeriveKeyMaterial(context.MasterKey, "catalog-encryption");
        var associatedData = Encoding.UTF8.GetBytes(GetAssociatedData(context, objectKey ?? context.ObjectKey));
        using var aesGcm = new AesGcm(encryptionKey, tagSizeInBytes: tag.Length);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }

    public string CreateRemoteHomeDirectory(RemoteScopeContext context) =>
        string.IsNullOrWhiteSpace(context.Prefix)
            ? $"chrono-storage://{context.Bucket}/{context.OwnerKey}"
            : $"chrono-storage://{context.Bucket}/{context.Prefix}/{context.OwnerKey}";

    public string CreateRemoteFilePath(RemoteScopeContext context) =>
        $"chrono-storage://{context.Bucket}/{context.ObjectKey}";

    private async Task EnsureBucketExistsAsync(RemoteScopeContext context, CancellationToken cancellationToken)
    {
        using var headRequest = CreateChronoStorageRequest(
            HttpMethod.Head,
            CreateChronoStorageUri(context, $"api/buckets/{Uri.EscapeDataString(context.Bucket)}"),
            context.StaticBearerToken);
        using var headResponse = await GetCatalogApiClient(context).SendAsync(headRequest, cancellationToken);
        if (headResponse.StatusCode != HttpStatusCode.NotFound)
        {
            headResponse.EnsureSuccessStatusCode();
            return;
        }

        using var createRequest = CreateChronoStorageRequest(
            HttpMethod.Post,
            CreateChronoStorageUri(context, "api/buckets"),
            context.StaticBearerToken,
            JsonContent.Create(new { name = context.Bucket }));
        using var createResponse = await GetCatalogApiClient(context).SendAsync(createRequest, cancellationToken);
        if (createResponse.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        createResponse.EnsureSuccessStatusCode();
    }

    private ChronoStorageApiEndpoint ResolveApiEndpoint()
    {
        var baseUrl = !string.IsNullOrWhiteSpace(_options.NyxProxyBaseUrl)
            ? _options.NyxProxyBaseUrl.Trim()
            : _nyxIdAppAuthOptions?.Authority?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException(
                "Cli:App:Connectors:ChronoStorage:NyxProxyBaseUrl or Cli:App:NyxId:Authority must be a valid absolute URL.");
        }

        var slug = _options.NyxProxyServiceSlug?.Trim();
        if (string.IsNullOrWhiteSpace(slug))
        {
            throw new InvalidOperationException(
                "Cli:App:Connectors:ChronoStorage:NyxProxyServiceSlug is required.");
        }

        return new ChronoStorageApiEndpoint(
            NormalizeBaseUri(baseUri),
            $"api/v1/proxy/s/{Uri.EscapeDataString(slug)}");
    }

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

    private HttpClient GetCatalogApiClient(RemoteScopeContext context) =>
        _httpClientFactory.CreateClient(NyxProxyHttpClientName);

    private async Task<byte[]?> TryDownloadEncryptedCoreAsync(
        RemoteScopeContext context,
        string objectKey,
        CancellationToken cancellationToken)
    {
        DownloadAttemptOutcome? lastFailure = null;
        for (var attempt = 1; attempt <= MaxDownloadUrlAttempts; attempt++)
        {
            using var request = CreateChronoStorageRequest(
                HttpMethod.Get,
                CreateChronoStorageUri(
                    context,
                    $"api/buckets/{Uri.EscapeDataString(context.Bucket)}/presigned-url",
                    $"key={Uri.EscapeDataString(objectKey)}&expiresIn={context.PresignedUrlExpiresInSeconds}"),
                context.StaticBearerToken);
            using var response = await GetCatalogApiClient(context).SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            var envelope = await response.Content.ReadFromJsonAsync<ChronoStorageEnvelope<PresignedUrlPayload>>(cancellationToken);
            var downloadUrl = string.IsNullOrWhiteSpace(envelope?.Data?.PresignedUrl)
                ? envelope?.Data?.Url?.Trim()
                : envelope.Data.PresignedUrl.Trim();
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                throw new InvalidOperationException("Chrono-storage presigned URL response did not include a usable download URL.");
            }

            var downloadUri = ResolveDownloadUri(context, downloadUrl);
            var outcome = await TryDownloadPayloadAsync(context, downloadUri, cancellationToken);
            if (outcome.Payload != null)
            {
                return outcome.Payload;
            }

            lastFailure = outcome;
            if (!ShouldRetryDownload(outcome) || attempt >= MaxDownloadUrlAttempts)
            {
                throw CreateDownloadFailureException(objectKey, outcome, attempt);
            }
        }

        throw CreateDownloadFailureException(
            objectKey,
            lastFailure ?? DownloadAttemptOutcome.FromStatusCode(HttpStatusCode.NotFound),
            MaxDownloadUrlAttempts);
    }

    private async Task<DownloadAttemptOutcome> TryDownloadPayloadAsync(
        RemoteScopeContext context,
        Uri downloadUri,
        CancellationToken cancellationToken)
    {
        var plans = BuildDownloadAttemptPlans(context, downloadUri);
        for (var index = 0; index < plans.Count; index++)
        {
            var plan = plans[index];
            try
            {
                using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, downloadUri);
                using var downloadResponse = await plan.Client.SendAsync(downloadRequest, cancellationToken);
                if (downloadResponse.IsSuccessStatusCode)
                {
                    return DownloadAttemptOutcome.FromPayload(
                        await downloadResponse.Content.ReadAsByteArrayAsync(cancellationToken));
                }

                if (index + 1 < plans.Count && ShouldRetryWithAlternateClient(downloadResponse.StatusCode))
                {
                    continue;
                }

                return DownloadAttemptOutcome.FromStatusCode(downloadResponse.StatusCode);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                if (index + 1 < plans.Count)
                {
                    continue;
                }

                return DownloadAttemptOutcome.FromException(exception);
            }
        }

        return DownloadAttemptOutcome.FromStatusCode(HttpStatusCode.NotFound);
    }

    private IReadOnlyList<DownloadAttemptPlan> BuildDownloadAttemptPlans(RemoteScopeContext context, Uri downloadUri)
    {
        if (IsSameAuthority(downloadUri, context.BaseUri))
        {
            return
            [
                new DownloadAttemptPlan(GetCatalogApiClient(context)),
            ];
        }

        var plans = new List<DownloadAttemptPlan>
        {
            new(_httpClientFactory.CreateClient()),
        };

        if (ShouldRetryWithNyxAuth(context, downloadUri))
        {
            plans.Add(new DownloadAttemptPlan(GetCatalogApiClient(context)));
        }

        return plans;
    }

    private static bool ShouldRetryWithAlternateClient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.NotFound ||
        statusCode == HttpStatusCode.Unauthorized ||
        statusCode == HttpStatusCode.Forbidden ||
        statusCode == HttpStatusCode.TooManyRequests ||
        statusCode == HttpStatusCode.BadGateway ||
        statusCode == HttpStatusCode.ServiceUnavailable ||
        statusCode == HttpStatusCode.GatewayTimeout;

    private static bool ShouldRetryDownload(DownloadAttemptOutcome outcome) =>
        outcome.Error is HttpRequestException or TaskCanceledException ||
        (outcome.StatusCode.HasValue && ShouldRetryWithAlternateClient(outcome.StatusCode.Value));

    private static bool ShouldRetryWithNyxAuth(RemoteScopeContext context, Uri downloadUri)
    {
        if (!string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(context.BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(downloadUri.Host, context.BaseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return downloadUri.Host.EndsWith($".{context.BaseUri.Host}", StringComparison.OrdinalIgnoreCase);
    }

    private static Exception CreateDownloadFailureException(
        string objectKey,
        DownloadAttemptOutcome outcome,
        int attempts)
    {
        if (outcome.StatusCode == HttpStatusCode.NotFound)
        {
            return new CatalogDownloadUnavailableException(
                $"Chrono-storage download URL returned 404 for object '{objectKey}' after {attempts} attempt(s). The catalog object exists but could not be downloaded.");
        }

        if (outcome.StatusCode.HasValue)
        {
            return new InvalidOperationException(
                $"Chrono-storage download request failed with HTTP {(int)outcome.StatusCode.Value} for object '{objectKey}' after {attempts} attempt(s).");
        }

        return new InvalidOperationException(
            $"Chrono-storage download request failed for object '{objectKey}' after {attempts} attempt(s).",
            outcome.Error);
    }

    private static Uri ResolveDownloadUri(RemoteScopeContext context, string downloadUrl)
    {
        var trimmed = downloadUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri;
        }

        if (Uri.TryCreate(context.BaseUri, trimmed, out var relativeUri))
        {
            return relativeUri;
        }

        throw new InvalidOperationException(
            $"Chrono-storage presigned URL response returned an invalid download URL '{downloadUrl}'.");
    }

    private static bool IsSameAuthority(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static Uri CreateChronoStorageUri(RemoteScopeContext context, string relativePath, string? query = null)
    {
        var builder = new UriBuilder(new Uri(context.BaseUri, CombineRelativePath(context.ApiRoutePrefix, relativePath)))
        {
            Query = query ?? string.Empty,
        };
        return builder.Uri;
    }

    private static Uri NormalizeBaseUri(Uri baseUri) => new($"{baseUri.ToString().TrimEnd('/')}/", UriKind.Absolute);

    private static string CombineRelativePath(string prefix, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return relativePath;
        }

        return $"{prefix.TrimEnd('/')}/{relativePath.TrimStart('/')}";
    }

    private static string NormalizePrefix(string? prefix) =>
        string.Join(
            '/',
            (prefix ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static IEnumerable<string> EnumerateCandidateObjectKeys(RemoteScopeContext context)
    {
        yield return context.ObjectKey;
        foreach (var legacyObjectKey in context.LegacyObjectKeys)
            yield return legacyObjectKey;
    }

    private static string CreateOwnerKey(string scopeId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopeId.Trim()))).ToLowerInvariant();

    private static string CreateLegacyOwnerKey(string scopeId, string masterKey)
    {
        var ownerKeyBytes = HMACSHA256.HashData(
            DeriveKeyMaterial(masterKey, "catalog-owner"),
            Encoding.UTF8.GetBytes(scopeId.Trim()));
        return Convert.ToHexString(ownerKeyBytes).ToLowerInvariant();
    }

    private static byte[] DeriveKeyMaterial(string masterKey, string purpose)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(masterKey));
        return HMACSHA256.HashData(seed, Encoding.UTF8.GetBytes(purpose));
    }

    private static string GetAssociatedData(RemoteScopeContext context, string objectKey) =>
        $"{context.Bucket}|{objectKey}|v1";

    internal sealed record RemoteScopeContext(
        AppScopeContext Scope,
        Uri BaseUri,
        string ApiRoutePrefix,
        string Bucket,
        string Prefix,
        string MasterKey,
        string OwnerKey,
        string ObjectKey,
        IReadOnlyList<string> LegacyObjectKeys,
        int PresignedUrlExpiresInSeconds,
        bool CreateBucketIfMissing,
        string? StaticBearerToken);

    internal sealed record DownloadedCatalogPayload(
        byte[] Payload,
        string ObjectKey);

    private sealed record DownloadAttemptPlan(
        HttpClient Client);

    private sealed class DownloadAttemptOutcome
    {
        private DownloadAttemptOutcome(byte[]? payload, HttpStatusCode? statusCode, Exception? error)
        {
            Payload = payload;
            StatusCode = statusCode;
            Error = error;
        }

        public byte[]? Payload { get; }
        public HttpStatusCode? StatusCode { get; }
        public Exception? Error { get; }

        public static DownloadAttemptOutcome FromPayload(byte[] payload) => new(payload, null, null);
        public static DownloadAttemptOutcome FromStatusCode(HttpStatusCode statusCode) => new(null, statusCode, null);
        public static DownloadAttemptOutcome FromException(Exception exception) => new(null, null, exception);
    }

    private sealed record ChronoStorageApiEndpoint(
        Uri BaseUri,
        string ApiRoutePrefix);

    private sealed class ChronoStorageEnvelope<T>
    {
        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }

    private sealed class PresignedUrlPayload
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("presignedUrl")]
        public string PresignedUrl { get; set; } = string.Empty;
    }

    private sealed class EncryptedCatalogEnvelope
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

    private sealed class CatalogDownloadUnavailableException : InvalidOperationException
    {
        public CatalogDownloadUnavailableException(string message)
            : base(message)
        {
        }
    }
}
