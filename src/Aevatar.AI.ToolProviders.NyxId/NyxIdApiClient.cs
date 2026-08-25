using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.Configuration;
using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed record NyxIdSessionRefreshResult(
    bool Succeeded,
    string? AccessToken = null,
    string? RefreshToken = null,
    int? ExpiresIn = null,
    string? Detail = null);

public sealed record NyxIdDelegationRefreshResult(
    bool Succeeded,
    string? AccessToken = null,
    string? TokenType = null,
    long? ExpiresIn = null,
    string? Scope = null,
    string? Detail = null,
    int HttpStatus = 0,
    string? ProviderErrorCode = null);

public sealed record NyxIdProxyBinaryResponse(
    bool Succeeded,
    byte[] Content,
    string? ContentType = null,
    string? FileName = null,
    string? Detail = null,
    int HttpStatus = 0);

public sealed record NyxIdProxyTextResponse(
    bool Succeeded,
    string Content,
    string? Detail = null,
    int HttpStatus = 0,
    string? Location = null,
    string? ETag = null,
    TimeSpan? RetryAfter = null,
    string? RequestId = null,
    string? CorrelationId = null);

// Refactor (iter1535/cluster-issue-1535):
//   Old pattern: NyxID relay update failures collapsed to Detail/EditUnsupported strings.
//   New principle: the external adapter boundary normalizes failure kind and raw diagnostics once.
public sealed record NyxIdChannelRelayReplyResult(
    bool Succeeded,
    string? MessageId = null,
    string? PlatformMessageId = null,
    string? Detail = null,
    bool EditUnsupported = false,
    FailureKind FailureKind = FailureKind.Unspecified,
    TimeSpan? RetryAfter = null,
    int HttpStatus = 0,
    string? RawErrorKey = null,
    int RawErrorCode = 0)
{
    public static NyxIdChannelRelayReplyResult FailedUpdateValidation(string detail) =>
        new(
            false,
            Detail: detail,
            FailureKind: FailureKind.PermanentAdapterError,
            RawErrorKey: detail);
}

// Refactor (iter1535/cluster-issue-1535):
//   Old pattern: each caller parsed NyxID error JSON enough to build its own string.
//   New principle: one adapter-boundary envelope feeds typed classification and retry diagnostics.
internal sealed record NyxIdApiErrorEnvelope(
    string Detail,
    int? HttpStatus,
    string? RawErrorKey,
    int? RawErrorCode,
    TimeSpan? RetryAfter);

internal sealed record NyxIdProxyError(
    int HttpStatus,
    string ErrorKey,
    int ErrorCode,
    string? ApprovalRequestId = null,
    string? ApprovalMode = null)
{
    public bool IsAuthorizationRequired =>
        HttpStatus == 401 &&
        ErrorCode == 1001 &&
        string.Equals(ErrorKey, "unauthorized", StringComparison.OrdinalIgnoreCase);
}

/// <summary>HTTP client for calling NyxID REST API endpoints.</summary>
public sealed class NyxIdApiClient : IDisposable, INyxIdUserReadApi
{
    internal const int DelegationRefreshMaxResponseBytes = 16 * 1024;

    public bool HasPublicApiEndpoint =>
        !string.IsNullOrWhiteSpace(_options.EffectiveApiBaseUrl);

    private enum ProxyCredentialTransport
    {
        AuthorizationBearer,
        ApiKeyHeader,
    }

    /// <summary>
    /// Default <c>User-Agent</c> injected on every call to <see cref="ProxyRequestAsync"/>
    /// when the caller does not specify one in <c>extraHeaders</c>. GitHub's REST API rejects
    /// requests without a <c>User-Agent</c> with HTTP 403 ("Request forbidden by administrative
    /// rules") — see https://docs.github.com/en/rest/overview/resources-in-the-rest-api#user-agent-required.
    /// .NET's <c>HttpClient</c> does not set one by default; NyxID proxies the client's headers
    /// through to GitHub, so the absence at the .NET layer manifests as a GitHub 403 in
    /// production. CLI tools written against <c>reqwest</c> (e.g. <c>nyxid proxy request</c>)
    /// happen to send <c>reqwest/x.y</c> as their default and so never hit this.
    /// </summary>
    public const string DefaultProxyUserAgent = "aevatar-agent-builder";
    private const string ApiKeyHeaderName = "X-API-Key";
    private const string UserAgentHeaderName = "User-Agent";

    private readonly HttpClient _http;
    private readonly NyxIdToolOptions _options;
    private readonly ILogger _logger;
    private readonly bool _ownsHttpClient;
    private readonly bool _allowPublicTransportFallback;

    internal static bool TryParseProxyError(string? response, out NyxIdProxyError? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var outerDocument = JsonDocument.Parse(response);
            var outer = outerDocument.RootElement;
            if (outer.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (outer.TryGetProperty("error", out var errorMarker) &&
                errorMarker.ValueKind == JsonValueKind.True &&
                outer.TryGetProperty("status", out var statusProperty) &&
                statusProperty.TryGetInt32(out var status))
            {
                if (outer.TryGetProperty("body", out var bodyProperty) &&
                bodyProperty.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(bodyProperty.GetString()))
                {
                    try
                    {
                        using var bodyDocument = JsonDocument.Parse(bodyProperty.GetString()!);
                        if (TryParseProxyErrorBody(bodyDocument.RootElement, status, out error))
                            return true;
                    }
                    catch (JsonException)
                    {
                        // The typed outer envelope still proves an upstream HTTP failure.
                    }
                }

                error = new NyxIdProxyError(status, string.Empty, 0);
                return true;
            }

            return TryParseProxyErrorBody(outer, 0, out error);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseProxyErrorBody(
        JsonElement body,
        int httpStatus,
        out NyxIdProxyError? error)
    {
        error = null;
        if (body.ValueKind != JsonValueKind.Object)
            return false;

        var errorKey = TryGetString(body, "error");
        var errorCode = TryGetInt(body, "error_code");
        if (string.IsNullOrWhiteSpace(errorKey) || errorCode is null)
            return false;

        var requestId = TryGetString(body, "request_id") ??
                        TryGetString(body, "approval_request_id");
        var approvalMode = TryGetString(body, "approval_mode");
        error = new NyxIdProxyError(
            httpStatus,
            errorKey,
            errorCode.Value,
            requestId,
            approvalMode);
        return true;
    }

    public NyxIdApiClient(
        NyxIdToolOptions options,
        HttpClient? httpClient = null,
        ILogger<NyxIdApiClient>? logger = null)
        : this(
            options,
            httpClient,
            logger,
            allowPublicTransportFallback: httpClient is null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public NyxIdApiClient(
        NyxIdToolOptions options,
        HttpClient httpClient,
        NyxIdApiClientTransportPolicy transportPolicy,
        ILogger<NyxIdApiClient>? logger = null)
        : this(
            options,
            httpClient ?? throw new ArgumentNullException(nameof(httpClient)),
            logger,
            allowPublicTransportFallback: transportPolicy is not null)
    {
        ArgumentNullException.ThrowIfNull(transportPolicy);
    }

    private NyxIdApiClient(
        NyxIdToolOptions options,
        HttpClient? httpClient,
        ILogger<NyxIdApiClient>? logger,
        bool allowPublicTransportFallback)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        // Refactor (iter10/cluster-019):
        // Old: singleton DI registration could construct and permanently pin a raw HttpClient.
        // New: DI registers this as an AddHttpClient<T> typed client; only manual construction owns this fallback.
        _http = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
        });
        _ownsHttpClient = httpClient is null;
        _allowPublicTransportFallback = allowPublicTransportFallback;
        // Only a self-created client may be configured here: mutating Timeout on a caller-supplied
        // HttpClient throws once it has started a request, and its owner sets its own policy.
        if (_ownsHttpClient)
            _http.Timeout = _options.EffectiveMaxRequestDuration;
        _logger = logger ?? NullLogger<NyxIdApiClient>.Instance;
    }

    // ─── Account ───

    public Task<string> GetCurrentUserAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/users/me", ct);

    // Admin-gated user search (email -> user id).
    // Existing NyxID route; case-insensitive regex match on email. Returns {"users":[{id,email,role,...}],...}.
    public Task<string> SearchAdminUsersAsync(string token, string email, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/admin/users?search={Uri.EscapeDataString(email)}", ct);

    // ─── Catalog ───

    public Task<string> ListCatalogAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/catalog", ct);

    public Task<string> GetCatalogEntryAsync(string token, string slug, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/catalog/{Uri.EscapeDataString(slug)}", ct);

    // ─── AI Services (unified /keys) ───

    public Task<string> ListServicesAsync(string token, CancellationToken ct) =>
        GetAsync(token, NyxIdLlmCatalogRoutes.UserKeysPath, ct);

    public Task<string> GetServiceAsync(string token, string id, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/keys/{Uri.EscapeDataString(id)}", ct);

    // Secret-free authorization evidence projection (NyxID#1464); the only
    // route assistant-action postconditions may read for a user service.
    public Task<string> GetServiceAuthorizationAsync(string token, string id, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/keys/{Uri.EscapeDataString(id)}/authorization", ct);

    public Task<string> DeleteServiceAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/keys/{Uri.EscapeDataString(id)}", ct);

    public Task<string> CreateServiceAsync(string token, string body, CancellationToken ct) =>
        PostAsync(token, "/api/v1/keys", body, ct);

    internal Task<NyxIdProxyTextResponse> CreateServiceResponseAsync(
        string token,
        string body,
        CancellationToken ct) =>
        PostTextResponseAsync(token, "/api/v1/keys", body, ct);

    // ─── Session Refresh ───

    public async Task<NyxIdSessionRefreshResult> RefreshSessionAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return new NyxIdSessionRefreshResult(false, Detail: "missing_refresh_token");

        var response = await PostWithoutAuthAsync(
            "/api/v1/auth/refresh",
            JsonSerializer.Serialize(new { refresh_token = refreshToken.Trim() }),
            ct);

        if (TryParseErrorEnvelope(response, out var errorDetail))
            return new NyxIdSessionRefreshResult(false, Detail: errorDetail);

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;

            if (!root.TryGetProperty("access_token", out var accessTokenProp) ||
                accessTokenProp.ValueKind != JsonValueKind.String)
            {
                return new NyxIdSessionRefreshResult(false, Detail: "invalid_refresh_response missing_access_token");
            }

            var refreshTokenValue = root.TryGetProperty("refresh_token", out var refreshTokenProp) &&
                                    refreshTokenProp.ValueKind == JsonValueKind.String
                ? refreshTokenProp.GetString()
                : null;
            var expiresIn = root.TryGetProperty("expires_in", out var expiresInProp) &&
                            expiresInProp.ValueKind == JsonValueKind.Number
                ? expiresInProp.GetInt32()
                : (int?)null;

            return new NyxIdSessionRefreshResult(
                true,
                AccessToken: accessTokenProp.GetString(),
                RefreshToken: refreshTokenValue,
                ExpiresIn: expiresIn);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "NyxID session refresh returned invalid JSON");
            return new NyxIdSessionRefreshResult(false, Detail: "invalid_refresh_response invalid_json");
        }
    }

    public async Task<NyxIdDelegationRefreshResult> RefreshDelegationAsync(
        string delegationToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(delegationToken))
            return new NyxIdDelegationRefreshResult(false, Detail: "missing_delegation_token");

        var url = $"{GetPublicApiBaseUrl()}/api/v1/delegation/refresh";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", delegationToken.Trim());
        var response = await SendTextResponseAsync(
            request,
            DelegationRefreshMaxResponseBytes,
            ct);
        if (!response.Succeeded)
        {
            if (response.Detail is "content_length_exceeds_max_bytes" or "content_exceeds_max_bytes")
            {
                return new NyxIdDelegationRefreshResult(
                    false,
                    Detail: "delegation_refresh_response_too_large",
                    HttpStatus: response.HttpStatus);
            }

            return DelegationRefreshFailure(response.Content, response.HttpStatus);
        }

        if (TryReadDelegationRefreshError(response.Content, response.HttpStatus, out var refreshFailure))
            return refreshFailure;

        try
        {
            using var document = JsonDocument.Parse(response.Content);
            var root = document.RootElement;
            var accessToken = TryGetString(root, "access_token");
            var tokenType = TryGetString(root, "token_type");
            if (string.IsNullOrWhiteSpace(accessToken) ||
                string.IsNullOrWhiteSpace(tokenType) ||
                !string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("expires_in", out var expiresInProperty) ||
                expiresInProperty.ValueKind != JsonValueKind.Number ||
                !expiresInProperty.TryGetInt64(out var expiresIn) ||
                expiresIn <= 0 ||
                !root.TryGetProperty("scope", out var scopeProperty) ||
                scopeProperty.ValueKind != JsonValueKind.String)
            {
                return new NyxIdDelegationRefreshResult(
                    false,
                    Detail: "invalid_delegation_refresh_response");
            }

            return new NyxIdDelegationRefreshResult(
                true,
                accessToken,
                tokenType,
                expiresIn,
                scopeProperty.GetString() ?? string.Empty);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "NyxID delegation refresh returned invalid JSON");
            return new NyxIdDelegationRefreshResult(
                false,
                Detail: "invalid_delegation_refresh_response");
        }
    }

    private static NyxIdDelegationRefreshResult DelegationRefreshFailure(
        string response,
        int httpStatus)
    {
        TryReadProviderErrorCode(response, out var providerErrorCode);
        var detail = httpStatus > 0
            ? $"nyx_status={httpStatus}"
            : "delegation_refresh_transport_failure";
        if (providerErrorCode is not null)
            detail += $" provider_error={providerErrorCode}";

        return new NyxIdDelegationRefreshResult(
            false,
            Detail: detail,
            HttpStatus: httpStatus,
            ProviderErrorCode: providerErrorCode);
    }

    private static bool TryReadDelegationRefreshError(
        string response,
        int fallbackHttpStatus,
        out NyxIdDelegationRefreshResult failure)
    {
        failure = default!;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("error", out var errorProperty) ||
                errorProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.String))
            {
                return false;
            }

            var status = TryGetInt(root, "status") ?? fallbackHttpStatus;
            failure = DelegationRefreshFailure(response, status);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadProviderErrorCode(string response, out string? errorCode)
    {
        errorCode = null;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var candidate = TryGetString(root, "error");
            if (candidate is null && TryGetInt(root, "error_code") is { } numericCode)
                candidate = numericCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (candidate is null &&
                root.TryGetProperty("body", out var bodyProperty) &&
                bodyProperty.ValueKind == JsonValueKind.String)
            {
                candidate = TryReadNestedProviderErrorCode(bodyProperty.GetString());
            }

            errorCode = NormalizeProviderErrorCode(candidate);
            return errorCode is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? TryReadNestedProviderErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            return TryGetString(document.RootElement, "error") ??
                   TryGetInt(document.RootElement, "error_code")?.ToString(
                       System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static string? NormalizeProviderErrorCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        return normalized.Length <= 64 &&
               normalized.All(static character =>
                   char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            ? normalized
            : null;
    }

    // ─── Proxy ───

    public Task<string> ProxyRequestAsync(
        string token,
        string slug,
        string path,
        string method,
        string? body,
        Dictionary<string, string>? extraHeaders,
        CancellationToken ct) =>
        ProxyRequestCoreAsync(token, slug, userServiceId: null, path, method, body, extraHeaders, ct);

    public Task<string> ProxyRequestAsync(
        string token,
        string slug,
        string userServiceId,
        string path,
        string method,
        string? body,
        Dictionary<string, string>? extraHeaders,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userServiceId);
        return ProxyRequestCoreAsync(token, slug, userServiceId.Trim(), path, method, body, extraHeaders, ct);
    }

    public Task<NyxIdProxyTextResponse> ProxyRequestBoundedAsync(
        string token,
        string slug,
        string userServiceId,
        string path,
        string method,
        string? body,
        Dictionary<string, string>? extraHeaders,
        long maxBytes,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userServiceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        return ProxyRequestBoundedCoreAsync(
            token,
            slug,
            userServiceId.Trim(),
            path,
            method,
            body,
            extraHeaders,
            maxBytes,
            ct);
    }

    /// <summary>
    /// Sends one bounded proxy exchange through the configured public NyxID API endpoint.
    /// This method never selects <c>InternalApiBaseUrl</c> and never replays through the
    /// transport fallback path, so callers can use it for durable mutation recovery.
    /// </summary>
    public Task<NyxIdProxyTextResponse> ProxyPublicRequestBoundedAsync(
        string token,
        string slug,
        string userServiceId,
        string path,
        string method,
        string? body,
        Dictionary<string, string>? extraHeaders,
        long maxBytes,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userServiceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        return ProxyPublicRequestBoundedCoreAsync(
            token,
            slug,
            userServiceId.Trim(),
            path,
            method,
            body,
            extraHeaders,
            maxBytes,
            ct);
    }

    public Task<NyxIdProxyTextResponse> ProxyRequestBoundedWithApiKeyAsync(
        string apiKey,
        string slug,
        string userServiceId,
        string path,
        string method,
        string? body,
        long maxBytes,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(userServiceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        return ProxyRequestBoundedWithApiKeyCoreAsync(
            apiKey,
            slug,
            userServiceId.Trim(),
            path,
            method,
            body,
            maxBytes,
            ct);
    }

    public Task<NyxIdProxyTextResponse> GetLlmRouteModelsBoundedAsync(
        string token,
        LLMRouteKind routeKind,
        string? verifiedUserServiceId,
        string? verifiedServiceSlug,
        long maxBytes,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        if (routeKind == LLMRouteKind.NyxIdUserService)
        {
            var userServiceId = NormalizeExactRouteIdentity(
                verifiedUserServiceId,
                nameof(verifiedUserServiceId));
            var serviceSlug = NormalizeExactRouteIdentity(
                verifiedServiceSlug,
                nameof(verifiedServiceSlug));
            if (serviceSlug.Contains('/') || serviceSlug.Contains('\\'))
                throw new ArgumentException("NyxID service slug cannot contain path separators.", nameof(verifiedServiceSlug));

            return ProxyRequestBoundedAsync(
                token,
                serviceSlug,
                userServiceId,
                "models",
                HttpMethod.Get.Method,
                body: null,
                extraHeaders: null,
                maxBytes,
                ct);
        }

        if (routeKind != LLMRouteKind.Gateway)
            throw new ArgumentOutOfRangeException(nameof(routeKind), "LLM route kind is not supported.");
        if (!string.IsNullOrWhiteSpace(verifiedUserServiceId) ||
            !string.IsNullOrWhiteSpace(verifiedServiceSlug))
        {
            throw new ArgumentException("Gateway LLM routes cannot carry user-service identity.");
        }

        return GetGatewayModelsBoundedAsync(token, maxBytes, ct);
    }

    private async Task<NyxIdProxyTextResponse> GetGatewayModelsBoundedAsync(
        string token,
        long maxBytes,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{GetPublicApiBaseUrl()}{LLMSelectionPolicy.GatewayRoute}/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation(UserAgentHeaderName, DefaultProxyUserAgent);
        return await SendTextResponseAsync(request, maxBytes, ct);
    }

    private static string NormalizeExactRouteIdentity(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (!string.Equals(value, normalized, StringComparison.Ordinal) ||
            normalized.Any(char.IsControl))
        {
            throw new ArgumentException("NyxID route identity must be canonical.", parameterName);
        }

        return normalized;
    }

    internal async Task<NyxIdProxyTextResponse> ProxyRequestResponseAsync(
        string token,
        string slug,
        string userServiceId,
        string path,
        string method,
        string? body,
        Dictionary<string, string>? extraHeaders,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userServiceId);
        using var request = CreateProxyRequest(
            token,
            slug,
            userServiceId.Trim(),
            path,
            method,
            body,
            extraHeaders);
        return await SendTextResponseAsync(request, ct);
    }

    private async Task<string> ProxyRequestCoreAsync(
        string token,
        string slug,
        string? userServiceId,
        string path,
        string method,
        string? body,
        Dictionary<string, string>? extraHeaders,
        CancellationToken ct)
    {
        using var request = CreateProxyRequest(
            token,
            slug,
            userServiceId,
            path,
            method,
            body,
            extraHeaders);
        return await SendAsync(request, ct);
    }

    private async Task<NyxIdProxyTextResponse> ProxyRequestBoundedCoreAsync(
        string token,
        string slug,
        string userServiceId,
        string path,
        string method,
        string? body,
        Dictionary<string, string>? extraHeaders,
        long maxBytes,
        CancellationToken ct)
    {
        using var request = CreateProxyRequest(
            token,
            slug,
            userServiceId,
            path,
            method,
            body,
            extraHeaders);
        return await SendTextResponseAsync(request, maxBytes, ct);
    }

    private async Task<NyxIdProxyTextResponse> ProxyPublicRequestBoundedCoreAsync(
        string token,
        string slug,
        string userServiceId,
        string path,
        string method,
        string? body,
        Dictionary<string, string>? extraHeaders,
        long maxBytes,
        CancellationToken ct)
    {
        using var request = CreateProxyRequest(
            token,
            slug,
            userServiceId,
            path,
            method,
            body,
            extraHeaders,
            publicApiOnly: true,
            applyAmbientIdempotencyKey: false);
        return await SendTextResponseAsync(
            request,
            maxBytes,
            ct,
            allowPublicTransportFallback: false);
    }

    private async Task<NyxIdProxyTextResponse> ProxyRequestBoundedWithApiKeyCoreAsync(
        string apiKey,
        string slug,
        string userServiceId,
        string path,
        string method,
        string? body,
        long maxBytes,
        CancellationToken ct)
    {
        using var request = CreateApiKeyProxyRequest(
            apiKey,
            slug,
            userServiceId,
            path,
            method,
            body);
        return await SendTextResponseAsync(request, maxBytes, ct);
    }

    private HttpRequestMessage CreateApiKeyProxyRequest(
        string apiKey,
        string slug,
        string userServiceId,
        string path,
        string method,
        string? body)
    {
        var request = CreateProxyRequest(
            apiKey,
            slug,
            userServiceId,
            path,
            method,
            body,
            extraHeaders: null,
            ProxyCredentialTransport.ApiKeyHeader);
        return request;
    }

    private HttpRequestMessage CreateProxyRequest(
        string token,
        string slug,
        string? userServiceId,
        string path,
        string method,
        string? body,
        Dictionary<string, string>? extraHeaders,
        ProxyCredentialTransport credentialTransport = ProxyCredentialTransport.AuthorizationBearer,
        bool publicApiOnly = false,
        bool applyAmbientIdempotencyKey = true)
    {
        var url = BuildProxyUrl(slug, userServiceId, path, publicApiOnly);
        var httpMethod = new HttpMethod(method.ToUpperInvariant());
        var request = new HttpRequestMessage(httpMethod, url);
        if (credentialTransport == ProxyCredentialTransport.ApiKeyHeader)
            request.Headers.Add(ApiKeyHeaderName, token);
        else
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var callerSpecifiedUserAgent = ApplyExtraHeaders(request, extraHeaders);
        if (!callerSpecifiedUserAgent)
            request.Headers.TryAddWithoutValidation(UserAgentHeaderName, DefaultProxyUserAgent);

        if (!string.IsNullOrEmpty(body) &&
            httpMethod != HttpMethod.Get &&
            httpMethod != HttpMethod.Head)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (applyAmbientIdempotencyKey)
            ApplyIdempotencyKey(request, httpMethod);
        return request;
    }

    public async Task<string> ProxyRequestBinaryAsync(
        string token,
        string slug,
        string path,
        string method,
        byte[] body,
        string contentType,
        Dictionary<string, string>? extraHeaders,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        var baseUrl = GetTransportBaseUrl();
        var normalizedPath = path.TrimStart('/');
        var url = $"{baseUrl}/api/v1/proxy/s/{Uri.EscapeDataString(slug)}/{normalizedPath}";

        var httpMethod = new HttpMethod(method.ToUpperInvariant());
        using var request = new HttpRequestMessage(httpMethod, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var callerSpecifiedUserAgent = ApplyExtraHeaders(request, extraHeaders);
        if (!callerSpecifiedUserAgent)
            request.Headers.TryAddWithoutValidation(UserAgentHeaderName, DefaultProxyUserAgent);

        if (httpMethod != HttpMethod.Get && httpMethod != HttpMethod.Head)
            request.Content = new ByteArrayContent(body)
            {
                Headers = { ContentType = MediaTypeHeaderValue.Parse(contentType) },
            };

        ApplyIdempotencyKey(request, httpMethod);
        return await SendAsync(request, ct);
    }

    public async Task<string> ProxyRequestMultipartAsync(
        string token,
        string slug,
        string path,
        string method,
        IReadOnlyDictionary<string, string> formFields,
        string fileFieldName,
        string fileName,
        string fileContentType,
        Stream fileContent,
        Dictionary<string, string>? extraHeaders,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileFieldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileContentType);
        ArgumentNullException.ThrowIfNull(fileContent);

        var baseUrl = GetTransportBaseUrl();
        var normalizedPath = path.TrimStart('/');
        var url = $"{baseUrl}/api/v1/proxy/s/{Uri.EscapeDataString(slug)}/{normalizedPath}";

        var httpMethod = new HttpMethod(method.ToUpperInvariant());
        using var request = new HttpRequestMessage(httpMethod, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var callerSpecifiedUserAgent = ApplyExtraHeaders(request, extraHeaders);
        if (!callerSpecifiedUserAgent)
            request.Headers.TryAddWithoutValidation(UserAgentHeaderName, DefaultProxyUserAgent);

        if (httpMethod != HttpMethod.Get && httpMethod != HttpMethod.Head)
        {
            var multipart = new MultipartFormDataContent();
            foreach (var (key, value) in formFields)
                multipart.Add(new StringContent(value, Encoding.UTF8), key);

            var filePart = new StreamContent(fileContent);
            filePart.Headers.ContentType = MediaTypeHeaderValue.Parse(fileContentType);
            multipart.Add(filePart, fileFieldName, fileName);
            request.Content = multipart;
        }

        ApplyIdempotencyKey(request, httpMethod);
        return await SendAsync(request, ct, allowPublicTransportFallback: false);
    }

    public async Task<NyxIdProxyBinaryResponse> ProxyGetBinaryResponseAsync(
        string token,
        string slug,
        string path,
        Dictionary<string, string>? extraHeaders,
        CancellationToken ct) =>
        await ProxyGetBinaryResponseCoreAsync(
            token,
            slug,
            userServiceId: null,
            path,
            extraHeaders,
            NyxIdToolOptions.DefaultProxyFileArtifactMaxBytes,
            ct);

    public async Task<NyxIdProxyBinaryResponse> ProxyGetBinaryResponseAsync(
        string token,
        string slug,
        string userServiceId,
        string path,
        Dictionary<string, string>? extraHeaders,
        CancellationToken ct) =>
        await ProxyGetBinaryResponseCoreAsync(
            token,
            slug,
            userServiceId,
            path,
            extraHeaders,
            NyxIdToolOptions.DefaultProxyFileArtifactMaxBytes,
            ct);

    public async Task<NyxIdProxyBinaryResponse> ProxyGetBinaryResponseAsync(
        string token,
        string slug,
        string path,
        Dictionary<string, string>? extraHeaders,
        long maxBytes,
        CancellationToken ct) =>
        await ProxyGetBinaryResponseCoreAsync(
            token,
            slug,
            userServiceId: null,
            path,
            extraHeaders,
            maxBytes,
            ct);

    public async Task<NyxIdProxyBinaryResponse> ProxyGetBinaryResponseAsync(
        string token,
        string slug,
        string userServiceId,
        string path,
        Dictionary<string, string>? extraHeaders,
        long maxBytes,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userServiceId);
        return await ProxyGetBinaryResponseCoreAsync(
            token,
            slug,
            userServiceId.Trim(),
            path,
            extraHeaders,
            maxBytes,
            ct);
    }

    private async Task<NyxIdProxyBinaryResponse> ProxyGetBinaryResponseCoreAsync(
        string token,
        string slug,
        string? userServiceId,
        string path,
        Dictionary<string, string>? extraHeaders,
        long maxBytes,
        CancellationToken ct)
    {
        var url = BuildProxyUrl(slug, userServiceId, path);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var callerSpecifiedUserAgent = ApplyExtraHeaders(request, extraHeaders);
        if (!callerSpecifiedUserAgent)
            request.Headers.TryAddWithoutValidation(UserAgentHeaderName, DefaultProxyUserAgent);

        return await SendBinaryResponseAsync(request, NormalizeProxyFileArtifactMaxBytes(maxBytes), ct);
    }

    private string BuildProxyUrl(
        string slug,
        string? userServiceId,
        string path,
        bool publicApiOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(path);
        var baseUrl = publicApiOnly ? GetPublicApiBaseUrl() : GetTransportBaseUrl();
        var normalizedPath = path.TrimStart('/');
        var fragmentIndex = normalizedPath.IndexOf('#', StringComparison.Ordinal);
        if (fragmentIndex >= 0)
            normalizedPath = normalizedPath[..fragmentIndex];

        var queryIndex = normalizedPath.IndexOf('?', StringComparison.Ordinal);
        var resourcePath = queryIndex >= 0 ? normalizedPath[..queryIndex] : normalizedPath;
        var query = queryIndex >= 0 ? normalizedPath[(queryIndex + 1)..] : string.Empty;
        var url = $"{baseUrl}/api/v1/proxy/s/{Uri.EscapeDataString(slug.Trim())}/{resourcePath}";
        if (string.IsNullOrWhiteSpace(userServiceId))
            return query.Length == 0 ? url : $"{url}?{query}";

        // _nyxid_via is a NyxID-reserved routing fact. The exact server-selected
        // identity must be the only value sent, because NyxID resolves the first one.
        var businessQuery = string.Join(
            '&',
            query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(static part => !string.Equals(
                    part.Split('=', 2)[0],
                    "_nyxid_via",
                    StringComparison.Ordinal)));
        var exactRoute = $"_nyxid_via={Uri.EscapeDataString(userServiceId.Trim())}";
        return businessQuery.Length == 0
            ? $"{url}?{exactRoute}"
            : $"{url}?{exactRoute}&{businessQuery}";
    }

    // ─── SSH ───

    /// <summary>
    /// Executes a shell command on a remote SSH host through NyxID's SSH gateway.
    /// </summary>
    /// <param name="serviceIdOrSlug">NyxID service identifier or slug for an SSH-typed service (endpoint registered as <c>ssh://host:port</c>).</param>
    /// <param name="body">JSON body matching NyxID's <c>SshExecRequest</c>: <c>{ command, principal, timeout_secs }</c>.</param>
    /// <remarks>
    /// Mirrors <c>POST /api/v1/ssh/{service_id}/exec</c>. NyxID enforces a 1 MB output cap, a max 300s
    /// timeout, an 8192-char command length, and a built-in dangerous-command filter. Non-SSH services
    /// reject this route, so callers must filter to SSH-typed slugs before invoking (the agent tool
    /// surfaces this in its description so the LLM does not call HTTP-typed services here).
    /// </remarks>
    public Task<string> SshExecAsync(string token, string serviceIdOrSlug, string body, CancellationToken ct) =>
        PostTransportAsync(token, $"/api/v1/ssh/{Uri.EscapeDataString(serviceIdOrSlug)}/exec", body, ct);

    // ─── API Keys ───

    public Task<string> ListApiKeysAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/api-keys", ct);

    public Task<string> ListApiKeysAsync(
        string token,
        string organizationOwnerId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationOwnerId);
        return GetAsync(
            token,
            "/api/v1/api-keys?org_id=" + Uri.EscapeDataString(organizationOwnerId.Trim()),
            ct);
    }

    public Task<string> CreateApiKeyAsync(string token, string requestBody, CancellationToken ct) =>
        PostAsync(token, "/api/v1/api-keys", requestBody, ct);

    /// <summary>
    /// Returns the authenticated actor's permission-scoped personal and organization
    /// <c>UserService</c> inventory from NyxID's published API.
    /// </summary>
    public Task<string> ListUserServicesAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/user-services", ct);

    public Task<NyxIdProxyTextResponse> ListUserServicesBoundedAsync(
        string token,
        long maxBytes,
        CancellationToken ct) =>
        GetBoundedAsync(token, "/api/v1/user-services", maxBytes, ct);

    /// <summary>
    /// Requests NyxID's authoritative constrained API-key grants for an exact service set.
    /// The raw response is parsed by <see cref="NyxIdApiAccessResponseParser"/> at this adapter boundary.
    /// </summary>
    public Task<string> PlanApiKeyScopeAsync(
        string token,
        IReadOnlyCollection<string> selectedServiceIds,
        string? targetOrganizationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(selectedServiceIds);
        var serviceIds = selectedServiceIds.ToArray();
        if (serviceIds.Any(static id =>
                string.IsNullOrWhiteSpace(id) ||
                !string.Equals(id, id.Trim(), StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Selected NyxID service ids must be non-empty normalized values.",
                nameof(selectedServiceIds));
        }
        if (serviceIds.Distinct(StringComparer.Ordinal).Count() != serviceIds.Length)
        {
            throw new ArgumentException(
                "Selected NyxID service ids must not contain duplicates.",
                nameof(selectedServiceIds));
        }
        if (targetOrganizationId is not null &&
            (string.IsNullOrWhiteSpace(targetOrganizationId) ||
             !string.Equals(targetOrganizationId, targetOrganizationId.Trim(), StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The NyxID target organization id must be a normalized value when provided.",
                nameof(targetOrganizationId));
        }

        var requestBody = targetOrganizationId is null
            ? JsonSerializer.Serialize(new { selected_service_ids = serviceIds })
            : JsonSerializer.Serialize(new
            {
                selected_service_ids = serviceIds,
                target_org_id = targetOrganizationId,
            });
        return PostAsync(token, "/api/v1/api-keys/scope-plan", requestBody, ct);
    }

    // ─── Nodes ───

    public Task<string> ListNodesAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/nodes", ct);

    public Task<string> GetNodeAsync(string token, string id, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/nodes/{Uri.EscapeDataString(id)}", ct);

    public Task<string> ListPendingNodeCredentialsAsync(
        string token,
        string nodeId,
        bool? includeHistory,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        var path = $"/api/v1/nodes/{Uri.EscapeDataString(nodeId.Trim())}/credentials/pending";
        if (includeHistory.HasValue)
            path += $"?include_history={includeHistory.Value.ToString().ToLowerInvariant()}";
        return GetAsync(token, path, ct);
    }

    public Task<string> DeleteNodeAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/nodes/{Uri.EscapeDataString(id)}", ct);

    // ─── Service pools ───

    public Task<string> ListServicePoolsAsync(
        string token,
        string? organizationOwnerId,
        CancellationToken ct)
    {
        var path = "/api/v1/service-pools";
        if (!string.IsNullOrWhiteSpace(organizationOwnerId))
            path += "?org_id=" + Uri.EscapeDataString(organizationOwnerId.Trim());
        return GetAsync(token, path, ct);
    }

    public Task<string> GetServicePoolAsync(string token, string poolId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
        return GetAsync(token, $"/api/v1/service-pools/{Uri.EscapeDataString(poolId.Trim())}", ct);
    }

    // ─── Developer apps ───

    public Task<string> ListDeveloperOAuthClientsAsync(
        string token,
        string? organizationOwnerId,
        CancellationToken ct)
    {
        var path = "/api/v1/developer/oauth-clients";
        if (!string.IsNullOrWhiteSpace(organizationOwnerId))
            path += "?org_id=" + Uri.EscapeDataString(organizationOwnerId.Trim());
        return GetAsync(token, path, ct);
    }

    public Task<string> GetDeveloperOAuthClientAsync(
        string token,
        string clientId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return GetAsync(
            token,
            $"/api/v1/developer/oauth-clients/{Uri.EscapeDataString(clientId.Trim())}",
            ct);
    }

    // ─── OAuth broker bindings ───

    public Task<string> ListOAuthBrokerBindingsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/users/me/broker-bindings", ct);

    // ─── Service accounts ───

    public Task<string> ListServiceAccountsAsync(
        string token,
        string? organizationOwnerId,
        int page,
        int perPage,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(perPage, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(perPage, 100);
        var path = $"/api/v1/admin/service-accounts?page={page}&per_page={perPage}";
        if (!string.IsNullOrWhiteSpace(organizationOwnerId))
            path += "&org_id=" + Uri.EscapeDataString(organizationOwnerId.Trim());
        return GetAsync(token, path, ct);
    }

    public Task<string> GetServiceAccountAsync(
        string token,
        string serviceAccountId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceAccountId);
        return GetAsync(
            token,
            $"/api/v1/admin/service-accounts/{Uri.EscapeDataString(serviceAccountId.Trim())}",
            ct);
    }

    // ─── Approvals ───

    public Task<string> ListApprovalsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/approvals/requests", ct);

    public Task<string> DecideApprovalAsync(string token, string id, bool approved, CancellationToken ct) =>
        PostAsync(
            token,
            $"/api/v1/approvals/requests/{Uri.EscapeDataString(id)}/decide",
            JsonSerializer.Serialize(new { approved }),
            ct);

    public Task<string> ListApprovalServiceConfigsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/approvals/service-configs", ct);

    public Task<NyxIdProxyTextResponse> ListApprovalServiceConfigsBoundedAsync(
        string token,
        long maxBytes,
        CancellationToken ct) =>
        GetBoundedAsync(token, "/api/v1/approvals/service-configs", maxBytes, ct);

    // ─── Profile ───

    public Task<string> UpdateProfileAsync(string token, string body, CancellationToken ct) =>
        PutAsync(token, "/api/v1/users/me", body, ct);

    public Task<string> DeleteAccountAsync(string token, CancellationToken ct) =>
        DeleteAsync(token, "/api/v1/users/me", ct);

    public Task<string> ListConsentsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/users/me/consents", ct);

    public Task<string> RevokeConsentAsync(string token, string clientId, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/users/me/consents/{Uri.EscapeDataString(clientId)}", ct);

    // ─── MFA ───

    public Task<string> SetupMfaAsync(string token, CancellationToken ct) =>
        PostAsync(token, "/api/v1/mfa/setup", "{}", ct);

    public Task<string> VerifyMfaSetupAsync(string token, string body, CancellationToken ct) =>
        PostAsync(token, "/api/v1/mfa/verify-setup", body, ct);

    // ─── Sessions ───

    public Task<string> ListSessionsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/sessions", ct);

    // ─── Services (additions) ───

    public Task<string> UpdateServiceAsync(string token, string id, string body, CancellationToken ct) =>
        PutAsync(token, $"/api/v1/keys/{Uri.EscapeDataString(id)}", body, ct);

    public Task<string> UpdateServiceRouteAsync(string token, string id, string body, CancellationToken ct) =>
        PutAsync(token, $"/api/v1/user-services/{Uri.EscapeDataString(id)}", body, ct);

    internal Task<NyxIdProxyTextResponse> UpdateServiceRouteResponseAsync(
        string token,
        string id,
        string body,
        CancellationToken ct) =>
        PutTextResponseAsync(
            token,
            $"/api/v1/user-services/{Uri.EscapeDataString(id)}",
            body,
            ct);

    // ─── Proxy (additions) ───

    public Task<string> DiscoverProxyServicesAsync(string token, CancellationToken ct) =>
        GetAsync(token, NyxIdLlmCatalogRoutes.ProxyServicesPath, ct);

    public Task<string> GetMcpConfigAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/mcp/config", ct);

    // ─── API Keys (additions) ───

    public Task<string> GetApiKeyAsync(string token, string id, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/api-keys/{Uri.EscapeDataString(id)}", ct);

    // Secret-free authorization evidence projection (NyxID#1464); the only
    // route assistant-action postconditions may read for an agent API key.
    public Task<string> GetApiKeyAuthorizationAsync(string token, string id, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/api-keys/{Uri.EscapeDataString(id)}/authorization", ct);

    public Task<string> ListDurableGrantsAsync(
        string token,
        string apiKeyId,
        bool includeRevoked,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKeyId);
        var path = $"/api/v1/api-keys/{Uri.EscapeDataString(apiKeyId.Trim())}/durable-grants";
        if (includeRevoked)
            path += "?include_revoked=true";
        return GetAsync(token, path, ct);
    }

    public Task<string> RotateApiKeyAsync(string token, string id, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/api-keys/{Uri.EscapeDataString(id)}/rotate", "{}", ct);

    public Task<string> DeleteApiKeyAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/api-keys/{Uri.EscapeDataString(id)}", ct);

    public Task<string> UpdateApiKeyAsync(string token, string id, string body, CancellationToken ct) =>
        PutAsync(token, $"/api/v1/api-keys/{Uri.EscapeDataString(id)}", body, ct);

    // ─── Approvals (additions) ───

    public Task<string> CreateApprovalRequestAsync(string token, string body, CancellationToken ct) =>
        PostAsync(token, "/api/v1/approvals/requests", body, ct);

    public Task<string> GetApprovalAsync(string token, string id, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/approvals/requests/{Uri.EscapeDataString(id)}", ct);

    // Refactor (iter23/cluster-001-nyxid-tool-approval-polling):
    //   Old pattern: approval status reads were hidden inside a blocking remote handler loop.
    //   New principle: status reads are single-shot calls driven by actor self-continuation events.
    public Task<string> GetApprovalStatusAsync(string token, string id, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/approvals/requests/{Uri.EscapeDataString(id)}/status", ct);

    public Task<string> CreateExactServiceApprovalRequestAsync(
        string token,
        string body,
        CancellationToken ct) =>
        PostAsync(token, "/api/v1/approvals/exact-service/requests", body, ct);

    public Task<string> GetExactServiceApprovalStatusAsync(
        string token,
        string id,
        CancellationToken ct) =>
        GetAsync(token,
            $"/api/v1/approvals/exact-service/requests/{Uri.EscapeDataString(id)}/status", ct);

    public Task<string> RedeemExactServiceApprovalAsync(
        string token,
        string id,
        string body,
        CancellationToken ct) =>
        PostAsync(token,
            $"/api/v1/approvals/exact-service/requests/{Uri.EscapeDataString(id)}/redeem",
            body,
            ct);

    public Task<string> ListApprovalGrantsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/approvals/grants", ct);

    public Task<string> RevokeApprovalGrantAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/approvals/grants/{Uri.EscapeDataString(id)}", ct);

    public Task<string> SetApprovalConfigAsync(string token, string id, string body, CancellationToken ct) =>
        PutAsync(token, $"/api/v1/approvals/service-configs/{Uri.EscapeDataString(id)}", body, ct);

    // ─── Global Approval ───

    /// <summary>Enable or disable global approval protection via notification settings.</summary>
    public Task<string> SetGlobalApprovalAsync(string token, bool enabled, CancellationToken ct) =>
        PutAsync(token, "/api/v1/notifications/settings",
            enabled ? """{"approval_required":true}""" : """{"approval_required":false}""", ct);

    // ─── Endpoints ───

    public Task<string> ListEndpointsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/endpoints", ct);

    public Task<string> UpdateEndpointAsync(string token, string id, string body, CancellationToken ct) =>
        PutAsync(token, $"/api/v1/endpoints/{Uri.EscapeDataString(id)}", body, ct);

    public Task<string> DeleteEndpointAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/endpoints/{Uri.EscapeDataString(id)}", ct);

    // ─── External Keys ───

    public Task<string> ListExternalKeysAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/api-keys/external", ct);

    public Task<string> UpdateExternalKeyAsync(string token, string id, string body, CancellationToken ct) =>
        PutAsync(token, $"/api/v1/api-keys/external/{Uri.EscapeDataString(id)}", body, ct);

    public Task<string> DeleteExternalKeyAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/api-keys/external/{Uri.EscapeDataString(id)}", ct);

    // ─── Notifications ───

    public Task<string> GetNotificationSettingsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/notifications/settings", ct);

    public Task<NyxIdProxyTextResponse> GetNotificationSettingsBoundedAsync(
        string token,
        long maxBytes,
        CancellationToken ct) =>
        GetBoundedAsync(token, "/api/v1/notifications/settings", maxBytes, ct);

    public Task<string> UpdateNotificationSettingsAsync(string token, string body, CancellationToken ct) =>
        PutAsync(token, "/api/v1/notifications/settings", body, ct);

    public Task<string> TelegramLinkAsync(string token, CancellationToken ct) =>
        PostAsync(token, "/api/v1/notifications/telegram/link", "{}", ct);

    public Task<string> TelegramDisconnectAsync(string token, CancellationToken ct) =>
        DeleteAsync(token, "/api/v1/notifications/telegram", ct);

    // ─── Nodes (additions) ───

    public Task<string> GenerateNodeRegistrationTokenAsync(string token, string body, CancellationToken ct) =>
        PostAsync(token, "/api/v1/nodes/register-token", body, ct);

    public Task<string> RotateNodeTokenAsync(string token, string id, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/nodes/{Uri.EscapeDataString(id)}/rotate-token", "{}", ct);

    // ─── LLM ───

    public async Task<string> GetLlmServicesAsync(string token, CancellationToken ct)
    {
        var response = await GetAsync(token, "/api/v1/llm/services", ct).ConfigureAwait(false);
        return TryParseErrorStatus(response, out var status) && status == 404
            ? await GetAsync(token, "/api/v1/llm/status", ct).ConfigureAwait(false)
            : response;
    }

    public Task<string> ProvisionLlmServiceAsync(string token, string provisionEndpointId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionEndpointId);
        var candidate = provisionEndpointId.Trim();
        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate.Contains("..", StringComparison.Ordinal) ||
            candidate.Contains("://", StringComparison.Ordinal) ||
            candidate.Contains("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("Provision endpoint id must be a relative NyxID LLM service endpoint id.", nameof(provisionEndpointId));
        }

        var normalized = candidate.Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Provision endpoint id must be a relative NyxID LLM service endpoint id.", nameof(provisionEndpointId));

        return PostAsync(token, $"/api/v1/llm/services/{Uri.EscapeDataString(normalized)}", "{}", ct);
    }

    // ─── Providers ───

    public Task<string> ListProviderTokensAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/providers/my-tokens", ct);

    public Task<string> InitiateOAuthConnectAsync(string token, string providerId, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/providers/{Uri.EscapeDataString(providerId)}/connect/oauth", ct);

    public Task<string> InitiateDeviceCodeAsync(string token, string providerId, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/providers/{Uri.EscapeDataString(providerId)}/connect/device-code/initiate", "{}", ct);

    public Task<string> PollDeviceCodeAsync(string token, string providerId, string state, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/providers/{Uri.EscapeDataString(providerId)}/connect/device-code/poll",
            System.Text.Json.JsonSerializer.Serialize(new { state }), ct);

    public Task<string> DisconnectProviderAsync(string token, string providerId, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/providers/{Uri.EscapeDataString(providerId)}/disconnect", ct);

    // ─── User Provider Credentials ───

    public Task<string> GetUserCredentialsAsync(string token, string providerId, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/providers/{Uri.EscapeDataString(providerId)}/credentials", ct);

    public Task<string> SetUserCredentialsAsync(string token, string providerId, string body, CancellationToken ct) =>
        PutAsync(token, $"/api/v1/providers/{Uri.EscapeDataString(providerId)}/credentials", body, ct);

    public Task<string> DeleteUserCredentialsAsync(string token, string providerId, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/providers/{Uri.EscapeDataString(providerId)}/credentials", ct);

    // ─── Channel Bot Relay ───

    public Task<string> ListChannelBotsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/channel-bots", ct);

    public Task<string> GetChannelBotAsync(string token, string id, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/channel-bots/{Uri.EscapeDataString(id)}", ct);

    public Task<string> RegisterChannelBotAsync(string token, string body, CancellationToken ct) =>
        PostAsync(token, "/api/v1/channel-bots", body, ct);

    public Task<string> DeleteChannelBotAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/channel-bots/{Uri.EscapeDataString(id)}", ct);

    public Task<string> VerifyChannelBotAsync(string token, string id, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/channel-bots/{Uri.EscapeDataString(id)}/verify", "{}", ct);

    public Task<string> ListConversationRoutesAsync(string token, string? botId, CancellationToken ct) =>
        GetAsync(token, string.IsNullOrWhiteSpace(botId)
            ? "/api/v1/channel-conversations"
            : $"/api/v1/channel-conversations?bot_id={Uri.EscapeDataString(botId)}", ct);

    public Task<string> GetConversationRouteAsync(string token, string id, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/channel-conversations/{Uri.EscapeDataString(id)}", ct);

    public Task<string> CreateConversationRouteAsync(string token, string body, CancellationToken ct) =>
        PostAsync(token, "/api/v1/channel-conversations", body, ct);

    public Task<string> UpdateConversationRouteAsync(string token, string id, string body, CancellationToken ct) =>
        PutAsync(token, $"/api/v1/channel-conversations/{Uri.EscapeDataString(id)}", body, ct);

    public Task<string> DeleteConversationRouteAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/channel-conversations/{Uri.EscapeDataString(id)}", ct);

    // ─── Organizations ───

    public Task<string> ListOrgsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/orgs", ct);

    public Task<string> GetOrgAsync(string token, string id, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(id)}", ct);

    public Task<string> CreateOrgAsync(string token, string body, CancellationToken ct) =>
        PostAsync(token, "/api/v1/orgs", body, ct);

    public Task<string> UpdateOrgAsync(string token, string id, string body, CancellationToken ct) =>
        PatchAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(id)}", body, ct);

    public Task<string> DeleteOrgAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(id)}", ct);

    public Task<string> JoinOrgAsync(string token, string nonce, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/orgs/join/{Uri.EscapeDataString(nonce)}", "{}", ct);

    public Task<string> SetPrimaryOrgAsync(string token, string body, CancellationToken ct) =>
        PatchAsync(token, "/api/v1/users/me/primary-org", body, ct);

    // ─── Org Members ───

    public Task<string> ListOrgMembersAsync(string token, string orgId, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(orgId)}/members", ct);

    public Task<string> AddOrgMemberAsync(string token, string orgId, string body, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(orgId)}/members", body, ct);

    public Task<string> UpdateOrgMemberAsync(string token, string orgId, string memberId, string body, CancellationToken ct) =>
        PatchAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(orgId)}/members/{Uri.EscapeDataString(memberId)}", body, ct);

    public Task<string> RemoveOrgMemberAsync(string token, string orgId, string memberId, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(orgId)}/members/{Uri.EscapeDataString(memberId)}", ct);

    // ─── Org Invites ───

    public Task<string> ListOrgInvitesAsync(string token, string orgId, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(orgId)}/invites", ct);

    public Task<string> CreateOrgInviteAsync(string token, string orgId, string body, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(orgId)}/invites", body, ct);

    public Task<string> CancelOrgInviteAsync(string token, string orgId, string inviteId, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(orgId)}/invites/{Uri.EscapeDataString(inviteId)}", ct);

    // ─── Channel Events ───

    public Task<string> PushChannelEventAsync(string token, string conversationId, string body, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/channel-events/{Uri.EscapeDataString(conversationId)}", body, ct);

    /// <summary>
    /// Sends a channel relay reply with plain text only.
    /// </summary>
    /// <remarks>
    /// Kept as a thin wrapper over <see cref="SendChannelRelayReplyAsync"/> so legacy call sites that
    /// only need a text fallback continue to compile. New call sites should prefer the rich overload.
    /// </remarks>
    public Task<NyxIdChannelRelayReplyResult> SendChannelRelayTextReplyAsync(
        string token,
        string messageId,
        string text,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(new NyxIdChannelRelayReplyResult(false, Detail: "missing_reply_text"));

        return SendChannelRelayReplyAsync(token, messageId, new ChannelRelayReplyBody(text), ct);
    }

    /// <summary>
    /// Sends a channel relay reply with arbitrary body shape — text fallback and/or rich card metadata.
    /// </summary>
    /// <remarks>
    /// The <paramref name="body"/> is serialized as <c>{ message_id, reply: { text?, metadata: { card? } } }</c>.
    /// Transport-neutral callers (for example, the interactive reply dispatcher) use this overload to
    /// forward composer output verbatim; NyxID's per-platform adapter renders the card for each platform.
    /// </remarks>
    public async Task<NyxIdChannelRelayReplyResult> SendChannelRelayReplyAsync(
        string token,
        string messageId,
        ChannelRelayReplyBody body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (string.IsNullOrWhiteSpace(token))
            return new NyxIdChannelRelayReplyResult(false, Detail: "missing_access_token");
        if (string.IsNullOrWhiteSpace(messageId))
            return new NyxIdChannelRelayReplyResult(false, Detail: "missing_message_id");
        if (string.IsNullOrWhiteSpace(body.Text) && body.Metadata?.Card is null)
            return new NyxIdChannelRelayReplyResult(false, Detail: "missing_reply_payload");

        var response = await PostAsync(
            token,
            "/api/v1/channel-relay/reply",
            JsonSerializer.Serialize(new
            {
                message_id = messageId,
                reply = BuildReplyNode(body),
            }),
            ct);

        if (TryParseErrorEnvelope(response, out var errorDetail))
            return new NyxIdChannelRelayReplyResult(false, Detail: errorDetail);

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            return new NyxIdChannelRelayReplyResult(
                true,
                MessageId: root.TryGetProperty("message_id", out var replyMessageId) && replyMessageId.ValueKind == JsonValueKind.String
                    ? replyMessageId.GetString()
                    : null,
                PlatformMessageId: root.TryGetProperty("platform_message_id", out var platformMessageId) &&
                                   platformMessageId.ValueKind == JsonValueKind.String
                    ? platformMessageId.GetString()
                    : null);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Nyx channel relay reply returned invalid JSON");
            return new NyxIdChannelRelayReplyResult(false, Detail: "invalid_channel_relay_reply_response");
        }
    }

    private static object BuildReplyNode(ChannelRelayReplyBody body)
    {
        var hasText = !string.IsNullOrWhiteSpace(body.Text);
        var hasCard = body.Metadata?.Card is not null;

        if (hasText && hasCard)
            return new { text = body.Text, metadata = new { card = body.Metadata!.Card } };
        if (hasText)
            return new { text = body.Text };

        return new { metadata = new { card = body.Metadata!.Card } };
    }

    /// <summary>
    /// Edits a previously sent channel-relay reply so the downstream platform sees updated content
    /// (per NyxID #480 / #483: <c>POST /api/v1/channel-relay/reply/update</c>).
    /// </summary>
    /// <param name="platformMessageId">
    /// The upstream platform-owned message identifier (for Lark, the <c>om_xxx</c> value) returned
    /// by a prior send call.
    /// </param>
    /// <remarks>
    /// Callers must treat <see cref="NyxIdChannelRelayReplyResult.FailureKind"/> as the authoritative
    /// control-flow classification. <see cref="NyxIdChannelRelayReplyResult.EditUnsupported"/> is a
    /// compatibility signal for platforms that explicitly reject message edits.
    /// </remarks>
    // Refactor (iter1535/cluster-issue-1535):
    //   Old pattern: update callers treated all failed edits as strings or generic retry cases.
    //   New principle: update response parsing emits one typed result for continuation policy.
    public async Task<NyxIdChannelRelayReplyResult> UpdateChannelRelayReplyAsync(
        string token,
        string platformMessageId,
        ChannelRelayReplyBody body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (string.IsNullOrWhiteSpace(token))
            return NyxIdChannelRelayReplyResult.FailedUpdateValidation("missing_access_token");
        if (string.IsNullOrWhiteSpace(platformMessageId))
            return NyxIdChannelRelayReplyResult.FailedUpdateValidation("missing_platform_message_id");
        if (string.IsNullOrWhiteSpace(body.Text) && body.Metadata?.Card is null)
            return NyxIdChannelRelayReplyResult.FailedUpdateValidation("missing_reply_payload");

        var response = await PostAsync(
            token,
            "/api/v1/channel-relay/reply/update",
            JsonSerializer.Serialize(new
            {
                message_id = platformMessageId,
                reply = BuildReplyNode(body),
            }),
            ct);

        if (TryParseStructuredErrorEnvelope(response, out var error))
        {
            var editUnsupported = IsEditUnsupported(error);
            return new NyxIdChannelRelayReplyResult(
                false,
                Detail: error.Detail,
                EditUnsupported: editUnsupported,
                FailureKind: ClassifyUpdateFailure(error),
                RetryAfter: error.RetryAfter,
                HttpStatus: error.HttpStatus ?? 0,
                RawErrorKey: error.RawErrorKey,
                RawErrorCode: error.RawErrorCode ?? 0);
        }

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var upstream = root.TryGetProperty("upstream_message_id", out var upstreamProp) &&
                           upstreamProp.ValueKind == JsonValueKind.String
                ? upstreamProp.GetString()
                : null;
            return new NyxIdChannelRelayReplyResult(
                true,
                MessageId: null,
                PlatformMessageId: upstream);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Nyx channel relay reply update returned invalid JSON");
            return new NyxIdChannelRelayReplyResult(
                false,
                Detail: "invalid_channel_relay_reply_update_response",
                FailureKind: FailureKind.PermanentAdapterError);
        }
    }

    /// <summary>
    /// Text-only convenience wrapper over
    /// <see cref="UpdateChannelRelayReplyAsync(string, string, ChannelRelayReplyBody, CancellationToken)"/>.
    /// </summary>
    public Task<NyxIdChannelRelayReplyResult> UpdateChannelRelayTextReplyAsync(
        string token,
        string platformMessageId,
        string text,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(NyxIdChannelRelayReplyResult.FailedUpdateValidation("missing_reply_text"));

        return UpdateChannelRelayReplyAsync(token, platformMessageId, new ChannelRelayReplyBody(text), ct);
    }

    // ─── Admin Invite Codes ───

    public Task<string> ListInviteCodesAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/admin/invite-codes", ct);

    public Task<string> CreateInviteCodeAsync(string token, string body, CancellationToken ct) =>
        PostAsync(token, "/api/v1/admin/invite-codes", body, ct);

    public Task<string> DeactivateInviteCodeAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/admin/invite-codes/{Uri.EscapeDataString(id)}", ct);

    // ─── API Key Bindings ───

    public Task<string> BindApiKeyAsync(string token, string keyId, string body, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/api-keys/{Uri.EscapeDataString(keyId)}/bindings", body, ct);

    // ─── HTTP helpers ───

    private string GetTransportBaseUrl() =>
        _options.EffectiveTransportBaseUrl?.TrimEnd('/') ??
        throw new InvalidOperationException("NyxID transport base URL is not configured.");

    private string GetPublicApiBaseUrl() =>
        _options.EffectiveApiBaseUrl?.TrimEnd('/') ??
        throw new InvalidOperationException("NyxID public API base URL is not configured.");

    // Canonical RFC 8707 resource indicator for one exact proxied service.
    // The shape mirrors the proxy transport routes above and the pinned
    // service-access-review contract (…/api/v1/proxy/s/{slug}).
    internal string BuildServiceProxyResourceUri(string serviceSlug) =>
        $"{GetPublicApiBaseUrl()}/api/v1/proxy/s/{Uri.EscapeDataString(serviceSlug)}";

    private static bool ApplyExtraHeaders(
        HttpRequestMessage request,
        Dictionary<string, string>? extraHeaders)
    {
        var callerSpecifiedUserAgent = false;
        if (extraHeaders == null)
            return false;

        foreach (var (key, value) in extraHeaders)
        {
            request.Headers.TryAddWithoutValidation(key, value);
            if (string.Equals(key, UserAgentHeaderName, StringComparison.OrdinalIgnoreCase))
                callerSpecifiedUserAgent = true;
        }

        return callerSpecifiedUserAgent;
    }

    internal static string NormalizeExactProxyPath(string relativePath)
    {
        var candidate = relativePath.Trim();
        var withoutLeadingSlash = candidate.TrimStart('/');
        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate.StartsWith("//", StringComparison.Ordinal) ||
            Uri.TryCreate(withoutLeadingSlash, UriKind.Absolute, out _) ||
            candidate.Contains('?', StringComparison.Ordinal) ||
            candidate.Contains('#', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("invalid_relative_path");
        }

        var segments = candidate.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new InvalidOperationException("invalid_relative_path");
        var normalized = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded is "." or "..")
                throw new InvalidOperationException("invalid_relative_path");
            normalized.Add(Uri.EscapeDataString(decoded));
        }

        return string.Join('/', normalized);
    }

    private static void ApplyIdempotencyKey(HttpRequestMessage request, HttpMethod httpMethod)
    {
        if (httpMethod == HttpMethod.Get ||
            httpMethod == HttpMethod.Head ||
            httpMethod == HttpMethod.Options ||
            request.Headers.Contains("Idempotency-Key"))
        {
            return;
        }

        var key = AgentToolRequestContext.IdempotencyKey;
        if (!string.IsNullOrWhiteSpace(key))
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key.Trim());
    }

    internal async Task<string> GetAsync(string token, string path, CancellationToken ct)
    {
        var url = $"{GetPublicApiBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await SendAsync(request, ct);
    }

    private async Task<NyxIdProxyTextResponse> GetBoundedAsync(
        string token,
        string path,
        long maxBytes,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var url = $"{GetPublicApiBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await SendTextResponseAsync(request, maxBytes, ct);
    }

    internal async Task<string> PostAsync(string token, string path, string body, CancellationToken ct)
        => (await PostTextResponseAsync(token, path, body, ct)).Content;

    private async Task<NyxIdProxyTextResponse> PostTextResponseAsync(
        string token,
        string path,
        string body,
        CancellationToken ct)
    {
        var url = $"{GetPublicApiBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await SendTextResponseAsync(request, ct);
    }

    internal async Task<string> PostWithoutAuthAsync(string path, string body, CancellationToken ct)
    {
        var url = $"{GetPublicApiBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await SendAsync(request, ct);
    }

    internal async Task<string> PatchAsync(string token, string path, string body, CancellationToken ct)
    {
        var url = $"{GetPublicApiBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Patch, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await SendAsync(request, ct);
    }

    internal async Task<string> PutAsync(string token, string path, string body, CancellationToken ct)
        => (await PutTextResponseAsync(token, path, body, ct)).Content;

    private async Task<NyxIdProxyTextResponse> PutTextResponseAsync(
        string token,
        string path,
        string body,
        CancellationToken ct)
    {
        var url = $"{GetPublicApiBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await SendTextResponseAsync(request, ct);
    }

    internal async Task<string> DeleteAsync(string token, string path, CancellationToken ct)
    {
        var url = $"{GetPublicApiBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await SendAsync(request, ct);
    }

    private async Task<string> PostTransportAsync(
        string token,
        string path,
        string body,
        CancellationToken ct)
    {
        var url = $"{GetTransportBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await SendAsync(request, ct);
    }

    private async Task<string> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct,
        bool allowPublicTransportFallback = true) =>
        (await SendTextResponseAsync(request, ct, allowPublicTransportFallback)).Content;

    private async Task<NyxIdProxyTextResponse> SendTextResponseAsync(
        HttpRequestMessage request,
        CancellationToken ct,
        bool allowPublicTransportFallback = true)
    {
        try
        {
            using var response = await SendWithPublicTransportFallbackAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                allowPublicTransportFallback,
                ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "NyxID API request failed: {Method} -> {Status}",
                    request.Method, (int)response.StatusCode);
                var retryAfter = response.Headers.RetryAfter?.Delta;
                var retryAfterJson = retryAfter.HasValue
                    ? $", \"retry_after_seconds\": {(int)Math.Ceiling(retryAfter.Value.TotalSeconds)}"
                    : string.Empty;
                return new NyxIdProxyTextResponse(
                    false,
                    $"{{\"error\": true, \"status\": {(int)response.StatusCode}, \"body\": {EscapeJsonString(content)}{retryAfterJson}}}",
                    Detail: "http_error",
                    HttpStatus: (int)response.StatusCode);
            }

            return new NyxIdProxyTextResponse(
                true,
                content,
                HttpStatus: (int)response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a control-flow signal, not an HTTP failure. Wrapping it as
            // {"error":true,"message":"A task was canceled."} would swallow per-call hard
            // timeouts that callers (e.g. NyxIdSshExecTool) install on top of the LLM run's
            // CT. Let the exception bubble so callers can map their own cancellation source
            // to a clearer error payload (PR #562 SSH timeout incident, 2026-05-08).
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "NyxID API request exception: {Method} exceptionType={ExceptionType}",
                request.Method,
                ex.GetType().Name);
            return new NyxIdProxyTextResponse(
                false,
                """{"error":true,"status":0,"body":""}""",
                Detail: "proxy_transport_failure");
        }
    }

    private async Task<NyxIdProxyBinaryResponse> SendBinaryResponseAsync(
        HttpRequestMessage request,
        long maxBytes,
        CancellationToken ct)
    {
        try
        {
            using var response = await SendWithPublicTransportFallbackAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                allowPublicTransportFallback: true,
                ct);
            var contentType = response.Content.Headers.ContentType?.ToString();
            var fileName = ResolveContentDispositionFileName(response);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await ReadBoundedContentAsync(
                    response.Content,
                    Math.Min(maxBytes, 64 * 1024),
                    ct);
                _logger.LogWarning(
                    "NyxID binary proxy request failed: {Method} -> {Status}",
                    request.Method, (int)response.StatusCode);
                return new NyxIdProxyBinaryResponse(
                    false,
                    [],
                    contentType,
                    fileName,
                    Detail: Encoding.UTF8.GetString(errorContent.Content),
                    HttpStatus: (int)response.StatusCode);
            }

            if (response.Content.Headers.ContentLength is { } contentLength &&
                contentLength > maxBytes)
            {
                _logger.LogWarning(
                    "NyxID binary proxy response content length exceeded max bytes: {Method} length={Length} max={MaxBytes}",
                    request.Method, contentLength, maxBytes);
                return new NyxIdProxyBinaryResponse(
                    false,
                    [],
                    contentType,
                    fileName,
                    Detail: "content_length_exceeds_max_bytes",
                    HttpStatus: (int)response.StatusCode);
            }

            var content = await ReadBoundedContentAsync(response.Content, maxBytes, ct);
            if (content.Exceeded)
            {
                _logger.LogWarning(
                    "NyxID binary proxy response exceeded max bytes while reading: {Method} max={MaxBytes}",
                    request.Method, maxBytes);
                return new NyxIdProxyBinaryResponse(
                    false,
                    [],
                    contentType,
                    fileName,
                    Detail: "content_exceeds_max_bytes",
                    HttpStatus: (int)response.StatusCode);
            }

            return new NyxIdProxyBinaryResponse(
                true,
                content.Content,
                contentType,
                fileName,
                HttpStatus: (int)response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "NyxID binary proxy request exception: {Method} exceptionType={ExceptionType}",
                request.Method,
                ex.GetType().Name);
            return new NyxIdProxyBinaryResponse(
                false,
                [],
                Detail: "binary_proxy_transport_failure");
        }
    }

    private async Task<NyxIdProxyTextResponse> SendTextResponseAsync(
        HttpRequestMessage request,
        long maxBytes,
        CancellationToken ct,
        bool allowPublicTransportFallback = true)
    {
        try
        {
            using var response = await SendWithPublicTransportFallbackAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                allowPublicTransportFallback,
                ct);
            var location = ReadBoundedResponseHeader(response, "Location", 2_048);
            var etag = ReadBoundedResponseHeader(response, "ETag", 512);
            var retryAfter = ResolveRetryAfter(response.Headers.RetryAfter);
            var requestId = ReadBoundedResponseHeader(response, "X-Request-ID", 128);
            var correlationId = ReadBoundedResponseHeader(response, "X-Correlation-ID", 128);
            if (response.Content.Headers.ContentLength is { } contentLength &&
                contentLength > maxBytes)
            {
                _logger.LogWarning(
                    "NyxID bounded proxy response content length exceeded max bytes: {Method} length={Length} max={MaxBytes}",
                    request.Method,
                    contentLength,
                    maxBytes);
                return new NyxIdProxyTextResponse(
                    false,
                    string.Empty,
                    Detail: "content_length_exceeds_max_bytes",
                    HttpStatus: (int)response.StatusCode,
                    Location: location,
                    ETag: etag,
                    RetryAfter: retryAfter,
                    RequestId: requestId,
                    CorrelationId: correlationId);
            }

            var content = await ReadBoundedContentAsync(response.Content, maxBytes, ct);
            if (content.Exceeded)
            {
                _logger.LogWarning(
                    "NyxID bounded proxy response exceeded max bytes while reading: {Method} max={MaxBytes}",
                    request.Method,
                    maxBytes);
                return new NyxIdProxyTextResponse(
                    false,
                    string.Empty,
                    Detail: "content_exceeds_max_bytes",
                    HttpStatus: (int)response.StatusCode,
                    Location: location,
                    ETag: etag,
                    RetryAfter: retryAfter,
                    RequestId: requestId,
                    CorrelationId: correlationId);
            }

            var text = Encoding.UTF8.GetString(content.Content);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "NyxID bounded proxy request failed: {Method} -> {Status}",
                    request.Method,
                    (int)response.StatusCode);
                return new NyxIdProxyTextResponse(
                    false,
                    text,
                    Detail: "http_error",
                    HttpStatus: (int)response.StatusCode,
                    Location: location,
                    ETag: etag,
                    RetryAfter: retryAfter,
                    RequestId: requestId,
                    CorrelationId: correlationId);
            }

            return new NyxIdProxyTextResponse(
                true,
                text,
                HttpStatus: (int)response.StatusCode,
                Location: location,
                ETag: etag,
                RetryAfter: retryAfter,
                RequestId: requestId,
                CorrelationId: correlationId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "NyxID bounded proxy request exception: {Method} exceptionType={ExceptionType}",
                request.Method,
                ex.GetType().Name);
            return new NyxIdProxyTextResponse(
                false,
                string.Empty,
                Detail: "bounded_proxy_transport_failure");
        }
    }

    private static string? ReadBoundedResponseHeader(
        HttpResponseMessage response,
        string name,
        int maxLength)
    {
        if (!response.Headers.TryGetValues(name, out var values))
            return null;

        using var enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext())
            return null;
        var value = enumerator.Current?.Trim();
        if (enumerator.MoveNext() ||
            string.IsNullOrWhiteSpace(value) ||
            value.Length > maxLength ||
            value.Any(static character => char.IsControl(character)))
        {
            return null;
        }

        return value;
    }

    private static TimeSpan? ResolveRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retryAfter?.Date is not { } date)
            return null;
        var remaining = date - DateTimeOffset.UtcNow;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    private async Task<HttpResponseMessage> SendWithPublicTransportFallbackAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        bool allowPublicTransportFallback,
        CancellationToken ct)
    {
        ReplayableRequest? replay = null;
        Uri? fallbackUri = null;
        if (_allowPublicTransportFallback &&
            allowPublicTransportFallback &&
            TryBuildPublicTransportFallbackUri(request.RequestUri, out var resolvedFallbackUri))
        {
            replay = await ReplayableRequest.CreateAsync(request, ct);
            fallbackUri = resolvedFallbackUri;
        }

        if (replay is null || fallbackUri is null)
            return await _http.SendAsync(request, completionOption, ct);

        if (!NyxIdTransportFallbackPolicy.CanReplayAfterResponseHeaderTimeout(request.Method))
        {
            try
            {
                return await _http.SendAsync(request, completionOption, ct);
            }
            catch (HttpRequestException ex) when (
                NyxIdTransportFailureClassifier.IsPreConnectFailure(ex) &&
                !ct.IsCancellationRequested)
            {
                LogPreConnectFallback(request.Method, ex);
                return await SendFallbackAsync(replay, fallbackUri, completionOption, ct);
            }
        }

        // HttpClient's timeout is per SendAsync call. Keep one outer budget across the primary and
        // fallback attempts while using a shorter, independent budget only until primary headers.
        using var totalRequestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_http.Timeout != Timeout.InfiniteTimeSpan)
            totalRequestCts.CancelAfter(_http.Timeout);
        using var primaryAttemptCts = CancellationTokenSource.CreateLinkedTokenSource(totalRequestCts.Token);
        var primaryTimeout = _options.EffectiveInternalApiFallbackTimeout;
        primaryAttemptCts.CancelAfter(primaryTimeout);

        HttpResponseMessage primaryResponse;
        try
        {
            primaryResponse = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                primaryAttemptCts.Token);
        }
        catch (OperationCanceledException) when (
            !ct.IsCancellationRequested &&
            !totalRequestCts.IsCancellationRequested &&
            primaryAttemptCts.IsCancellationRequested)
        {
            _logger.LogWarning(
                "NyxID primary transport returned no response headers within {TimeoutSeconds}s for safe {Method}; retrying the configured public transport once",
                primaryTimeout.TotalSeconds,
                request.Method);
            return await SendFallbackAsync(
                replay,
                fallbackUri,
                completionOption,
                totalRequestCts.Token);
        }
        catch (HttpRequestException ex) when (
            NyxIdTransportFailureClassifier.IsPreConnectFailure(ex) &&
            !ct.IsCancellationRequested &&
            !totalRequestCts.IsCancellationRequested)
        {
            LogPreConnectFallback(request.Method, ex);
            return await SendFallbackAsync(
                replay,
                fallbackUri,
                completionOption,
                totalRequestCts.Token);
        }

        if (completionOption == HttpCompletionOption.ResponseContentRead)
        {
            try
            {
                await primaryResponse.Content.LoadIntoBufferAsync(totalRequestCts.Token);
            }
            catch
            {
                primaryResponse.Dispose();
                throw;
            }
        }

        return primaryResponse;
    }

    private void LogPreConnectFallback(HttpMethod method, HttpRequestException exception) =>
        _logger.LogWarning(
            "NyxID primary transport could not establish a connection for {Method} ({Failure}); retrying the configured public transport once",
            method,
            exception.HttpRequestError);

    private async Task<HttpResponseMessage> SendFallbackAsync(
        ReplayableRequest replay,
        Uri fallbackUri,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        using var fallbackRequest = replay.CreateRequest(fallbackUri);
        return await _http.SendAsync(fallbackRequest, completionOption, ct);
    }

    private bool TryBuildPublicTransportFallbackUri(Uri? requestUri, out Uri fallbackUri)
    {
        fallbackUri = null!;
        if (requestUri is null || !requestUri.IsAbsoluteUri ||
            string.IsNullOrWhiteSpace(_options.EffectiveTransportBaseUrl) ||
            string.IsNullOrWhiteSpace(_options.PublicTransportFallbackBaseUrl) ||
            !Uri.TryCreate(
                _options.EffectiveTransportBaseUrl.TrimEnd('/') + "/",
                UriKind.Absolute,
                out var primaryBaseUri) ||
            !Uri.TryCreate(
                _options.PublicTransportFallbackBaseUrl.TrimEnd('/') + "/",
                UriKind.Absolute,
                out var publicBaseUri) ||
            Uri.Compare(
                requestUri,
                primaryBaseUri,
                UriComponents.SchemeAndServer,
                UriFormat.Unescaped,
                StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }

        var primaryPath = primaryBaseUri.AbsolutePath.TrimEnd('/');
        var requestPath = requestUri.AbsolutePath;
        if (primaryPath.Length > 0 &&
            !string.Equals(requestPath, primaryPath, StringComparison.Ordinal) &&
            !requestPath.StartsWith(primaryPath + "/", StringComparison.Ordinal))
        {
            return false;
        }

        var relativePath = primaryPath.Length == 0
            ? requestPath
            : requestPath[primaryPath.Length..];
        var publicPath = publicBaseUri.AbsolutePath.TrimEnd('/');
        var fallbackValue =
            $"{publicBaseUri.GetLeftPart(UriPartial.Authority)}{publicPath}{relativePath}{requestUri.Query}";
        if (!Uri.TryCreate(fallbackValue, UriKind.Absolute, out var candidateFallbackUri) ||
            AreSameTransportUri(requestUri, candidateFallbackUri))
        {
            return false;
        }

        fallbackUri = candidateFallbackUri;
        return true;
    }

    private static bool AreSameTransportUri(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port &&
        string.Equals(left.PathAndQuery, right.PathAndQuery, StringComparison.Ordinal);

    private sealed record ReplayableRequest(
        HttpMethod Method,
        Version Version,
        HttpVersionPolicy VersionPolicy,
        IReadOnlyList<KeyValuePair<string, string[]>> Headers,
        byte[]? Content,
        IReadOnlyList<KeyValuePair<string, string[]>> ContentHeaders)
    {
        public static async Task<ReplayableRequest> CreateAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            var content = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(ct);
            return new ReplayableRequest(
                request.Method,
                request.Version,
                request.VersionPolicy,
                SnapshotHeaders(request.Headers, skipHost: true),
                content,
                request.Content is null
                    ? []
                    : SnapshotHeaders(request.Content.Headers, skipHost: false));
        }

        public HttpRequestMessage CreateRequest(Uri uri)
        {
            var request = new HttpRequestMessage(Method, uri)
            {
                Version = Version,
                VersionPolicy = VersionPolicy,
            };
            RestoreHeaders(request.Headers, Headers);

            if (Content is not null)
            {
                request.Content = new ByteArrayContent(Content);
                request.Content.Headers.Clear();
                RestoreHeaders(request.Content.Headers, ContentHeaders);
            }

            return request;
        }

        private static IReadOnlyList<KeyValuePair<string, string[]>> SnapshotHeaders(
            System.Net.Http.Headers.HttpHeaders headers,
            bool skipHost) =>
            headers
                .Where(header => !skipHost ||
                                 !string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                .Select(header => new KeyValuePair<string, string[]>(header.Key, header.Value.ToArray()))
                .ToArray();

        private static void RestoreHeaders(
            System.Net.Http.Headers.HttpHeaders target,
            IEnumerable<KeyValuePair<string, string[]>> headers)
        {
            foreach (var header in headers)
                target.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static string? ResolveContentDispositionFileName(HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        return NormalizeHeaderFileName(disposition?.FileNameStar) ??
               NormalizeHeaderFileName(disposition?.FileName);
    }

    private static string? NormalizeHeaderFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static long NormalizeProxyFileArtifactMaxBytes(long maxBytes) =>
        maxBytes <= 0
            ? NyxIdToolOptions.DefaultProxyFileArtifactMaxBytes
            : Math.Min(maxBytes, NyxIdToolOptions.HardProxyFileArtifactMaxBytes);

    private static async Task<BoundedBinaryContent> ReadBoundedContentAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                break;

            total += read;
            if (total > maxBytes)
                return new BoundedBinaryContent([], Exceeded: true);

            memory.Write(buffer, 0, read);
        }

        return new BoundedBinaryContent(memory.ToArray(), Exceeded: false);
    }

    private static string EscapeJsonString(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    private static bool TryParseErrorEnvelope(string response, out string detail)
    {
        if (TryParseStructuredErrorEnvelope(response, out var error))
        {
            detail = error.Detail;
            return true;
        }

        detail = string.Empty;
        return false;
    }

    private readonly record struct BoundedBinaryContent(byte[] Content, bool Exceeded);

    private static bool TryParseStructuredErrorEnvelope(string response, out NyxIdApiErrorEnvelope error)
    {
        error = new NyxIdApiErrorEnvelope(string.Empty, null, null, null, null);
        if (string.IsNullOrWhiteSpace(response))
        {
            error = new NyxIdApiErrorEnvelope("empty_response", null, null, null, null);
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("error", out var errorProp) ||
                errorProp.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            var status = TryGetInt(root, "status");
            var body = root.TryGetProperty("body", out var bodyProp) &&
                       bodyProp.ValueKind == JsonValueKind.String
                ? bodyProp.GetString()
                : null;
            var message = root.TryGetProperty("message", out var messageProp) &&
                          messageProp.ValueKind == JsonValueKind.String
                ? messageProp.GetString()
                : null;

            var rawErrorKey = TryGetString(root, "error_key") ?? TryGetString(root, "error");
            var rawErrorCode = TryGetInt(root, "error_code");
            var retryAfter = TryGetRetryAfter(root);
            TryMergeBodyError(body, ref rawErrorKey, ref rawErrorCode, ref retryAfter);

            var detail = $"nyx_status={status?.ToString() ?? "unknown"}" +
                     (string.IsNullOrWhiteSpace(body) ? string.Empty : $" body={body}") +
                     (string.IsNullOrWhiteSpace(message) ? string.Empty : $" message={message}");
            error = new NyxIdApiErrorEnvelope(detail, status, rawErrorKey, rawErrorCode, retryAfter);
            return true;
        }
        catch (JsonException)
        {
            error = new NyxIdApiErrorEnvelope(
                $"invalid_error_envelope response_length={response.Length}",
                null,
                "invalid_error_envelope",
                null,
                null);
            return true;
        }
    }

    private static void TryMergeBodyError(
        string? body,
        ref string? rawErrorKey,
        ref int? rawErrorCode,
        ref TimeSpan? retryAfter)
    {
        if (string.IsNullOrWhiteSpace(body))
            return;

        try
        {
            using var bodyDocument = JsonDocument.Parse(body);
            var bodyRoot = bodyDocument.RootElement;
            rawErrorKey ??= TryGetString(bodyRoot, "error") ?? TryGetString(bodyRoot, "code");
            rawErrorCode ??= TryGetInt(bodyRoot, "error_code");
            retryAfter ??= TryGetRetryAfter(bodyRoot);
        }
        catch (JsonException)
        {
            rawErrorKey ??= "malformed_error_body";
        }
    }

    private static string? TryGetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static int? TryGetInt(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number &&
        prop.TryGetInt32(out var value)
            ? value
            : null;

    private static TimeSpan? TryGetRetryAfter(JsonElement root)
    {
        var seconds = TryGetInt(root, "retry_after_seconds") ?? TryGetInt(root, "retry_after");
        if (seconds is > 0)
            return TimeSpan.FromSeconds(seconds.Value);

        var milliseconds = TryGetInt(root, "retry_after_ms");
        return milliseconds is > 0 ? TimeSpan.FromMilliseconds(milliseconds.Value) : null;
    }

    private static bool IsEditUnsupported(NyxIdApiErrorEnvelope error) =>
        string.Equals(error.RawErrorKey, "edit_unsupported", StringComparison.OrdinalIgnoreCase) ||
        error.HttpStatus == 501;

    // Refactor (iter1535/cluster-issue-1535):
    //   Old pattern: actor continuation policy searched raw error summaries.
    //   New principle: the NyxID adapter maps known external error keys to channel FailureKind.
    private static FailureKind ClassifyUpdateFailure(NyxIdApiErrorEnvelope error)
    {
        if (IsEditUnsupported(error))
            return FailureKind.PermanentAdapterError;

        if (string.Equals(error.RawErrorKey, "platform_unavailable", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(error.RawErrorKey, "channel_platform_unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return FailureKind.PlatformUnavailable;
        }

        if (error.HttpStatus is 429 or >= 500)
            return FailureKind.TransientAdapterError;

        if (string.Equals(error.RawErrorKey, "rate_limited", StringComparison.OrdinalIgnoreCase))
            return FailureKind.TransientAdapterError;

        if (string.Equals(error.RawErrorKey, "validation_error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(error.RawErrorKey, "authentication_failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(error.RawErrorKey, "unauthorized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(error.RawErrorKey, "forbidden", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(error.RawErrorKey, "missing_access_token", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(error.RawErrorKey, "missing_platform_message_id", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(error.RawErrorKey, "missing_reply_payload", StringComparison.OrdinalIgnoreCase) ||
            error.HttpStatus is 400 or 401 or 403 or 404 or 422)
        {
            return FailureKind.PermanentAdapterError;
        }

        return FailureKind.PermanentAdapterError;
    }

    private static bool TryParseErrorStatus(string response, out int status)
    {
        status = 0;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("error", out var errorProp) ||
                errorProp.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("status", out var statusProp) ||
                statusProp.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            status = statusProp.GetInt32();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}
