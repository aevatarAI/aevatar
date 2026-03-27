using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageCatalogBlobClient
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
            CreateBucketIfMissing: _options.CreateBucketIfMissing,
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
                $"api/buckets/{Uri.EscapeDataString(context.Bucket)}/presigned-url",
                $"key={Uri.EscapeDataString(context.ObjectKey)}&expiresIn={context.PresignedUrlExpiresInSeconds}"),
            context);
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
        using var downloadRequest = IsSameAuthority(downloadUri, context.BaseUri)
            ? CreateChronoStorageRequest(HttpMethod.Get, downloadUri, context)
            : new HttpRequestMessage(HttpMethod.Get, downloadUri);
        using var downloadResponse = await GetDownloadClient(context, downloadUri).SendAsync(downloadRequest, cancellationToken);
        if (downloadResponse.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Chrono-storage download URL returned 404 for object '{context.ObjectKey}'. The catalog object exists but could not be downloaded.");
        }

        downloadResponse.EnsureSuccessStatusCode();
        return await downloadResponse.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task UploadAsync(
        RemoteScopeContext context,
        byte[] payload,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (context.CreateBucketIfMissing)
        {
            await EnsureBucketExistsAsync(context, cancellationToken);
        }

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
        using var response = await GetCatalogApiClient(context).SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
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
        using var response = await GetCatalogApiClient(context).SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    public string CreateRemoteHomeDirectory(RemoteScopeContext context) =>
        string.IsNullOrWhiteSpace(context.Prefix)
            ? $"chrono-storage://{context.Bucket}/{context.ScopeDirectory}"
            : $"chrono-storage://{context.Bucket}/{context.Prefix}/{context.ScopeDirectory}";

    public string CreateRemoteFilePath(RemoteScopeContext context) =>
        $"chrono-storage://{context.Bucket}/{context.ObjectKey}";

    private async Task EnsureBucketExistsAsync(RemoteScopeContext context, CancellationToken cancellationToken)
    {
        using var headRequest = CreateChronoStorageRequest(
            HttpMethod.Head,
            CreateChronoStorageUri(context, $"api/buckets/{Uri.EscapeDataString(context.Bucket)}"),
            context);
        using var headResponse = await GetCatalogApiClient(context).SendAsync(headRequest, cancellationToken);
        if (headResponse.StatusCode != HttpStatusCode.NotFound)
        {
            headResponse.EnsureSuccessStatusCode();
            return;
        }

        using var createRequest = CreateChronoStorageRequest(
            HttpMethod.Post,
            CreateChronoStorageUri(context, "api/buckets"),
            context,
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

    private HttpClient GetDownloadClient(RemoteScopeContext context, Uri downloadUri) =>
        IsSameAuthority(downloadUri, context.BaseUri)
            ? GetCatalogApiClient(context)
            : _httpClientFactory.CreateClient();

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

    internal sealed record RemoteScopeContext(
        AppScopeContext Scope,
        Uri BaseUri,
        string ApiRoutePrefix,
        string Bucket,
        string Prefix,
        string ScopeDirectory,
        string ObjectKey,
        int PresignedUrlExpiresInSeconds,
        bool CreateBucketIfMissing,
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

    private sealed class PresignedUrlPayload
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("presignedUrl")]
        public string PresignedUrl { get; set; } = string.Empty;
    }
}
