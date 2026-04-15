using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Infrastructure.Storage;

public sealed class ChronoStorageCatalogBlobClient
{
    public const string NyxProxyHttpClientName = "NyxProxyAuthorized";

    private readonly IAppScopeResolver _scopeResolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConnectorCatalogStorageOptions _options;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly NyxIdAppAuthOptions? _nyxIdAppAuthOptions;

    public ChronoStorageCatalogBlobClient(
        IAppScopeResolver scopeResolver,
        IHttpClientFactory httpClientFactory,
        IOptions<ConnectorCatalogStorageOptions> options,
        IHttpContextAccessor? httpContextAccessor = null,
        IOptions<NyxIdAppAuthOptions>? nyxIdAppAuthOptions = null)
    {
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _httpContextAccessor = httpContextAccessor;
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
        var scope = _scopeResolver.Resolve();
        if (scope is null)
        {
            throw new InvalidOperationException("Chrono-storage catalog access requires an authenticated or configured app scope.");
        }

        var normalizedPrefix = NormalizePrefix(prefix);
        var scopeDirectory = NormalizeScopeDirectory(scope.ScopeId);
        var objectKey = string.IsNullOrWhiteSpace(normalizedPrefix)
            ? $"{scopeDirectory}/{fileName}"
            : $"{normalizedPrefix}/{scopeDirectory}/{fileName}";

        return new RemoteScopeContext(
            Scope: scope,
            BaseUri: endpoint.BaseUri,
            ApiRoutePrefix: endpoint.ApiRoutePrefix,
            Bucket: bucket,
            Prefix: normalizedPrefix,
            ScopeDirectory: scopeDirectory,
            ObjectKey: objectKey,
            PresignedUrlExpiresInSeconds: Math.Clamp(_options.PresignedUrlExpiresInSeconds <= 0 ? 300 : _options.PresignedUrlExpiresInSeconds, 30, 3600),
            UseNyxProxy: endpoint.UseNyxProxy,
            StaticBearerToken: _options.StaticBearerToken?.Trim());
    }

    public async Task<byte[]?> TryDownloadAsync(
        RemoteScopeContext context,
        CancellationToken cancellationToken)
    {
        using var request = CreateChronoStorageRequest(
            HttpMethod.Get,
            CreateChronoStorageUri(
                context,
                $"api/buckets/{Uri.EscapeDataString(context.Bucket)}/objects/download",
                $"key={Uri.EscapeDataString(context.ObjectKey)}"),
            context);
        using var response = await SendChronoStorageRequestAsync(context, request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            ThrowChronoStorageFailure(context, response.StatusCode);
        }

        return await TryDownloadViaPresignedUrlAsync(context, cancellationToken);
    }

    private async Task<byte[]?> TryDownloadViaPresignedUrlAsync(
        RemoteScopeContext context,
        CancellationToken cancellationToken)
    {
        using var presignedRequest = CreateChronoStorageRequest(
            HttpMethod.Get,
            CreateChronoStorageUri(
                context,
                $"api/buckets/{Uri.EscapeDataString(context.Bucket)}/presigned-url",
                $"key={Uri.EscapeDataString(context.ObjectKey)}"),
            context);
        using var presignedResponse = await SendChronoStorageRequestAsync(context, presignedRequest, cancellationToken);
        if (presignedResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!presignedResponse.IsSuccessStatusCode)
        {
            ThrowChronoStorageFailure(context, presignedResponse.StatusCode);
        }

        var payload = await presignedResponse.Content.ReadFromJsonAsync<ChronoStorageEnvelope<PresignedUrlPayload>>(cancellationToken);
        var presignedUrl = payload?.Data?.Url?.Trim() ?? payload?.Data?.PresignedUrl?.Trim();
        if (string.IsNullOrWhiteSpace(presignedUrl))
        {
            throw new InvalidOperationException("Chrono-storage presigned-url response did not include a download URL.");
        }

        using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, presignedUrl);
        using var downloadResponse = await SendDirectRequestAsync(downloadRequest, cancellationToken);
        if (downloadResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!downloadResponse.IsSuccessStatusCode)
        {
            ThrowChronoStorageFailure(context, downloadResponse.StatusCode);
        }

        return await downloadResponse.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task UploadAsync(
        RemoteScopeContext context,
        byte[] payload,
        string contentType,
        CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var request = CreateChronoStorageRequest(
            HttpMethod.Post,
            CreateChronoStorageUri(
                context,
                $"api/buckets/{Uri.EscapeDataString(context.Bucket)}/objects",
                $"key={Uri.EscapeDataString(context.ObjectKey)}&contentType={Uri.EscapeDataString(contentType)}"),
            context,
            content);
        using var response = await SendChronoStorageRequestAsync(context, request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            ThrowChronoStorageFailure(context, response.StatusCode);
        }
    }

    public async Task<ListObjectsResult> ListObjectsAsync(
        RemoteScopeContext context,
        string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        var baseScope = string.IsNullOrWhiteSpace(context.Prefix)
            ? context.ScopeDirectory
            : $"{context.Prefix}/{context.ScopeDirectory}";
        var listPrefix = string.IsNullOrWhiteSpace(prefix)
            ? baseScope + "/"
            : $"{baseScope}/{prefix.TrimStart('/')}";

        var query = $"prefix={Uri.EscapeDataString(listPrefix)}";
        using var request = CreateChronoStorageRequest(
            HttpMethod.Get,
            CreateChronoStorageUri(
                context,
                $"api/buckets/{Uri.EscapeDataString(context.Bucket)}/objects",
                query),
            context);
        using var response = await SendChronoStorageRequestAsync(context, request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            ThrowChronoStorageFailure(context, response.StatusCode);
        }

        var envelope = await response.Content.ReadFromJsonAsync<ChronoStorageEnvelope<ListObjectsPayload>>(cancellationToken);
        var objects = envelope?.Data?.Objects ?? [];
        // Strip the scopeDirectory prefix (including any bucket-level prefix) so keys are relative
        // (e.g. "workflows/foo.yaml")
        var stripped = objects
            .Select(o => o with { Key = StripScopePrefix(o.Key, context.ScopeDirectory, context.Prefix) })
            .Where(o => !string.IsNullOrWhiteSpace(o.Key))
            .ToList();
        return new ListObjectsResult(stripped);
    }

    private static string StripScopePrefix(string key, string scopeDir, string? bucketPrefix = null)
    {
        var fullDir = string.IsNullOrWhiteSpace(bucketPrefix)
            ? scopeDir.TrimEnd('/')
            : $"{bucketPrefix.TrimEnd('/')}/{scopeDir.TrimEnd('/')}";
        var p = fullDir + "/";
        return key.StartsWith(p, StringComparison.Ordinal) ? key[p.Length..] : string.Empty;
    }

    public async Task DeleteIfExistsAsync(
        RemoteScopeContext context,
        CancellationToken cancellationToken)
    {
        using var request = CreateChronoStorageRequest(
            HttpMethod.Delete,
            CreateChronoStorageUri(
                context,
                $"api/buckets/{Uri.EscapeDataString(context.Bucket)}/objects",
                $"key={Uri.EscapeDataString(context.ObjectKey)}"),
            context);
        using var response = await SendChronoStorageRequestAsync(context, request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            ThrowChronoStorageFailure(context, response.StatusCode);
        }
    }

    public string CreateRemoteHomeDirectory(RemoteScopeContext context) =>
        string.IsNullOrWhiteSpace(context.Prefix)
            ? $"chrono-storage://{context.Bucket}/{context.ScopeDirectory}"
            : $"chrono-storage://{context.Bucket}/{context.Prefix}/{context.ScopeDirectory}";

    public string CreateRemoteFilePath(RemoteScopeContext context) =>
        $"chrono-storage://{context.Bucket}/{context.ObjectKey}";

    private ChronoStorageApiEndpoint ResolveApiEndpoint()
    {
        if (_options.UseNyxProxy)
        {
            var baseUrl = !string.IsNullOrWhiteSpace(_options.NyxProxyBaseUrl)
                ? _options.NyxProxyBaseUrl.Trim()
                : _nyxIdAppAuthOptions?.Authority?.Trim();
            if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            {
                throw new InvalidOperationException(
                    "Cli:App:Connectors:ChronoStorage:NyxProxyBaseUrl or Cli:App:NyxId:Authority must be a valid absolute URL when Nyx proxy mode is enabled.");
            }

            var slug = _options.NyxProxyServiceSlug?.Trim();
            if (string.IsNullOrWhiteSpace(slug))
            {
                throw new InvalidOperationException(
                    "Cli:App:Connectors:ChronoStorage:NyxProxyServiceSlug is required when Nyx proxy mode is enabled.");
            }

            return new ChronoStorageApiEndpoint(
                NormalizeBaseUri(baseUri),
                $"api/v1/proxy/s/{Uri.EscapeDataString(slug)}",
                UseNyxProxy: true);
        }

        var directBaseUrl = _options.BaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(directBaseUrl) || !Uri.TryCreate(directBaseUrl, UriKind.Absolute, out var directBaseUri))
        {
            throw new InvalidOperationException(
                "Cli:App:Connectors:ChronoStorage:BaseUrl must be a valid absolute URL when direct chrono-storage mode is enabled.");
        }

        return new ChronoStorageApiEndpoint(
            NormalizeBaseUri(directBaseUri),
            ApiRoutePrefix: string.Empty,
            UseNyxProxy: false);
    }

    private HttpRequestMessage CreateChronoStorageRequest(
        HttpMethod method,
        Uri uri,
        RemoteScopeContext context,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = content,
        };

        var bearerToken = ResolveBearerToken(context.StaticBearerToken);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return request;
    }

    private string? ResolveBearerToken(string? staticBearerToken)
    {
        var normalizedStaticToken = staticBearerToken?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedStaticToken))
        {
            return normalizedStaticToken;
        }

        var authorizationHeader = _httpContextAccessor?.HttpContext?.Request.Headers.Authorization.ToString().Trim();
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }

        const string bearerPrefix = "Bearer ";
        if (!authorizationHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorizationHeader[bearerPrefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private HttpClient GetCatalogApiClient(RemoteScopeContext context) =>
        context.UseNyxProxy
            ? _httpClientFactory.CreateClient(NyxProxyHttpClientName)
            : _httpClientFactory.CreateClient();

    private async Task<HttpResponseMessage> SendChronoStorageRequestAsync(
        RemoteScopeContext context,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetCatalogApiClient(context).SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChronoStorageServiceException(
                "Studio could not access chrono-storage-service because the request timed out. The service may not be enabled for this host, the service may be unavailable, or NyxID may be configured to require approval for every proxy request.",
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ChronoStorageServiceException(
                BuildChronoStorageFailureMessage(context, exception.StatusCode),
                exception.StatusCode,
                exception);
        }
    }

    private async Task<HttpResponseMessage> SendDirectRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChronoStorageServiceException(
                "Studio could not access chrono-storage-service because the request timed out. The service may not be enabled for this host, the service may be unavailable, or NyxID may be configured to require approval for every proxy request.",
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ChronoStorageServiceException(
                "Studio could not access chrono-storage-service. The service may not be enabled for this host, the service may be unavailable, or NyxID may be configured to require approval for every proxy request.",
                exception.StatusCode,
                exception);
        }
    }

    private static void ThrowChronoStorageFailure(RemoteScopeContext context, HttpStatusCode statusCode) =>
        throw new ChronoStorageServiceException(
            BuildChronoStorageFailureMessage(context, statusCode),
            statusCode);

    private static string BuildChronoStorageFailureMessage(RemoteScopeContext context, HttpStatusCode? statusCode)
    {
        const string prefix = "Studio could not access chrono-storage-service.";
        if (statusCode == HttpStatusCode.Forbidden && context.UseNyxProxy)
        {
            return $"{prefix} NyxID may be configured to require approval for every proxy request, the service may not be enabled for this account, or the service may be unavailable.";
        }

        if (statusCode == HttpStatusCode.Forbidden)
        {
            return $"{prefix} The current credentials may not have access, or the service may be unavailable.";
        }

        if (statusCode == HttpStatusCode.Unauthorized)
        {
            return $"{prefix} Sign in again or verify the configured service credentials.";
        }

        return $"{prefix} The service may not be enabled for this host, the service may be unavailable, or NyxID may be configured to require approval for every proxy request.";
    }

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

    private static string NormalizeScopeDirectory(string scopeId)
    {
        var normalized = string.Join(
            '/',
            (scopeId ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Chrono-storage catalog access requires a non-empty scope id.");
        }

        return normalized;
    }

    // --- List objects result ---

    public sealed record StorageObject(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("lastModified")] string? LastModified,
        [property: JsonPropertyName("size")] long? Size);

    public sealed record ListObjectsResult(IReadOnlyList<StorageObject> Objects);

    // --- Manifest DTOs (used by ExplorerEndpoints) ---

    public sealed class ManifestEntry
    {
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; set; }
    }

    public sealed class StorageManifest
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("files")] public List<ManifestEntry> Files { get; set; } = new();
    }

    public sealed record RemoteScopeContext(
        AppScopeContext Scope,
        Uri BaseUri,
        string ApiRoutePrefix,
        string Bucket,
        string Prefix,
        string ScopeDirectory,
        string ObjectKey,
        int PresignedUrlExpiresInSeconds,
        bool UseNyxProxy,
        string? StaticBearerToken);

    private sealed record ChronoStorageApiEndpoint(
        Uri BaseUri,
        string ApiRoutePrefix,
        bool UseNyxProxy);

    private sealed class ChronoStorageEnvelope<T>
    {
        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }

    private sealed class ListObjectsPayload
    {
        [JsonPropertyName("objects")]
        public List<StorageObject> Objects { get; set; } = new();
    }

    private sealed class PresignedUrlPayload
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("presignedUrl")]
        public string? PresignedUrl { get; set; }
    }
}
