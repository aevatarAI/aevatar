using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Configuration;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.NyxId;

internal sealed class NyxIdConnectLinkHttpClient : INyxIdConnectLinkPort
{
    internal const int MaxResponseBodyBytes = 64 * 1024;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private const string ConnectLinksPath = "/api/v1/connect-links";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NyxIdConnectLinkHttpClient> _logger;

    public NyxIdConnectLinkHttpClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<NyxIdConnectLinkHttpClient> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NyxIdConnectLinkCreated> CreateAsync(
        string bearerToken,
        NyxIdConnectLinkCreateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var serviceSlug = NormalizeRequired(request.ServiceSlug, nameof(request.ServiceSlug));
        if (request.ExpiresInSeconds is <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "expiresInSeconds must be positive when present.");
        if (request.CallbackUrl is { IsAbsoluteUri: false })
            throw new ArgumentException("callbackUrl must be an absolute URI when present.", nameof(request));
        if (request.CallbackUrl is { } callbackUrl &&
            (!IsHttpUrl(callbackUrl) ||
             string.IsNullOrWhiteSpace(callbackUrl.Host) ||
             !string.IsNullOrEmpty(callbackUrl.UserInfo) ||
             !string.IsNullOrEmpty(callbackUrl.Fragment)))
        {
            throw new ArgumentException(
                "callbackUrl must be an absolute HTTP or HTTPS URL without user info or a fragment.",
                nameof(request));
        }

        var payload = new CreateRequestBody
        {
            ServiceSlug = serviceSlug,
            Label = NormalizeOptional(request.Label),
            RequestedBy = NormalizeOptional(request.RequestedBy),
            CallbackUrl = request.CallbackUrl?.AbsoluteUri,
            ExpiresIn = request.ExpiresInSeconds,
        };
        using var httpRequest = BuildRequest(HttpMethod.Post, ConnectLinksPath, bearerToken);
        httpRequest.Content = JsonContent.Create(payload, options: SerializerOptions);

        var body = await SendAsync(httpRequest, "NyxID connect-link creation", ct).ConfigureAwait(false);
        var response = Deserialize<CreateResponseBody>(body, "connect-link creation");
        var connectLinkId = RequireResponseValue(response.Id, "id", "connect-link creation");
        var connectUrl = RequireResponseValue(response.ConnectUrl, "connect_url", "connect-link creation");
        if (!Uri.TryCreate(connectUrl, UriKind.Absolute, out var connectUri) || !IsHttpUrl(connectUri))
        {
            throw InvalidResponse("connect-link creation", "connect_url must be an absolute HTTP or HTTPS URL");
        }

        return new NyxIdConnectLinkCreated(
            connectLinkId,
            connectUrl,
            ParseTimestamp(response.ExpiresAt, "expires_at", "connect-link creation"));
    }

    public async Task<NyxIdConnectLinkSnapshot> GetAsync(
        string bearerToken,
        string connectLinkId,
        CancellationToken ct = default)
    {
        var normalizedLinkId = NormalizeRequired(connectLinkId, nameof(connectLinkId));
        using var request = BuildRequest(
            HttpMethod.Get,
            $"{ConnectLinksPath}/{Uri.EscapeDataString(normalizedLinkId)}",
            bearerToken);
        var body = await SendAsync(request, "NyxID connect-link status", ct).ConfigureAwait(false);
        var response = Deserialize<StatusResponseBody>(body, "connect-link status");
        var returnedLinkId = RequireResponseValue(response.Id, "id", "connect-link status");
        if (!string.Equals(returnedLinkId, normalizedLinkId, StringComparison.Ordinal))
            throw InvalidResponse("connect-link status", "id did not match the requested connect link");

        var status = response.Status switch
        {
            "pending" => NyxIdConnectLinkStatus.Pending,
            "completed" => NyxIdConnectLinkStatus.Completed,
            "expired" => NyxIdConnectLinkStatus.Expired,
            "cancelled" => NyxIdConnectLinkStatus.Cancelled,
            _ => throw InvalidResponse("connect-link status", "status is unknown"),
        };
        var userServiceId = NormalizeOptional(response.ConnectedService?.Id);
        if (status == NyxIdConnectLinkStatus.Completed && userServiceId is null)
            throw InvalidResponse("connect-link status", "completed status requires connected_service.id");
        if (status != NyxIdConnectLinkStatus.Completed && response.ConnectedService is not null)
            throw InvalidResponse("connect-link status", "connected_service is only valid for completed status");

        var completedAt = ParseOptionalTimestamp(
            response.CompletedAt,
            "completed_at",
            "connect-link status");
        if (status == NyxIdConnectLinkStatus.Completed && completedAt is null)
            throw InvalidResponse("connect-link status", "completed status requires completed_at");
        if (status != NyxIdConnectLinkStatus.Completed && completedAt is not null)
            throw InvalidResponse("connect-link status", "completed_at is only valid for completed status");

        return new NyxIdConnectLinkSnapshot(
            returnedLinkId,
            status,
            RequireResponseValue(response.ServiceSlug, "service_slug", "connect-link status"),
            ParseTimestamp(response.ExpiresAt, "expires_at", "connect-link status"),
            completedAt,
            userServiceId);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path, string bearerToken)
    {
        var normalizedBearerToken = NormalizeRequired(bearerToken, nameof(bearerToken));
        var apiBaseUrl = NyxIdEndpointResolver.ResolvePublicApiBaseUrl(_configuration);
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
            throw new InvalidOperationException("NyxID public API base URL is not configured.");

        var request = new HttpRequestMessage(method, $"{apiBaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", normalizedBearerToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<string> SendAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{Operation} endpoint returned {StatusCode}", operation, response.StatusCode);
                throw RequestRejected(operation, response.StatusCode);
            }

            return await ReadBoundedBodyAsync(
                    response.Content,
                    operation,
                    response.StatusCode,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            throw new NyxIdConnectLinkException(
                NyxIdConnectLinkFailureKind.Unavailable,
                statusCode: null,
                $"{operation} request timed out.",
                ex);
        }
        catch (NyxIdConnectLinkException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new NyxIdConnectLinkException(
                NyxIdConnectLinkFailureKind.Unavailable,
                ex.StatusCode,
                $"{operation} request failed.",
                ex);
        }
    }

    private static async Task<string> ReadBoundedBodyAsync(
        HttpContent content,
        string operation,
        HttpStatusCode statusCode,
        CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxResponseBodyBytes)
            throw ResponseTooLarge(operation, statusCode);

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[81920];
        var totalBytes = 0;
        while (true)
        {
            var maximumRead = Math.Min(buffer.Length, MaxResponseBodyBytes - totalBytes + 1);
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, maximumRead), ct).ConfigureAwait(false);
            if (bytesRead == 0)
                break;

            totalBytes += bytesRead;
            if (totalBytes > MaxResponseBodyBytes)
                throw ResponseTooLarge(operation, statusCode);
            body.Write(buffer, 0, bytesRead);
        }

        return Encoding.UTF8.GetString(body.GetBuffer(), 0, totalBytes);
    }

    private static T Deserialize<T>(string json, string operation)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, SerializerOptions)
                ?? throw InvalidResponse(operation, "response body must be an object");
        }
        catch (JsonException ex)
        {
            throw new NyxIdConnectLinkException(
                NyxIdConnectLinkFailureKind.ResponseInvalid,
                statusCode: null,
                $"NyxID {operation} response is invalid JSON.",
                ex);
        }
    }

    private static NyxIdConnectLinkException RequestRejected(string operation, HttpStatusCode statusCode) =>
        new(
            statusCode switch
            {
                HttpStatusCode.Unauthorized => NyxIdConnectLinkFailureKind.AuthenticationRejected,
                HttpStatusCode.Forbidden => NyxIdConnectLinkFailureKind.Forbidden,
                HttpStatusCode.NotFound => NyxIdConnectLinkFailureKind.NotFound,
                HttpStatusCode.TooManyRequests => NyxIdConnectLinkFailureKind.RateLimited,
                _ => NyxIdConnectLinkFailureKind.Unavailable,
            },
            statusCode,
            $"{operation} request failed with status {(int)statusCode}.");

    private static NyxIdConnectLinkException InvalidResponse(string operation, string detail) =>
        new(
            NyxIdConnectLinkFailureKind.ResponseInvalid,
            statusCode: null,
            $"NyxID {operation} response is invalid: {detail}.");

    private static NyxIdConnectLinkException ResponseTooLarge(string operation, HttpStatusCode statusCode) =>
        new(
            NyxIdConnectLinkFailureKind.ResponseTooLarge,
            statusCode,
            $"{operation} response exceeded {MaxResponseBodyBytes} bytes.");

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new ArgumentException($"{fieldName} is required.", fieldName);
        return normalized;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsHttpUrl(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static string RequireResponseValue(string? value, string fieldName, string operation) =>
        NormalizeOptional(value) ?? throw InvalidResponse(operation, $"{fieldName} is required");

    private static DateTimeOffset ParseTimestamp(string? value, string fieldName, string operation) =>
        ParseOptionalTimestamp(value, fieldName, operation)
        ?? throw InvalidResponse(operation, $"{fieldName} is required");

    private static DateTimeOffset? ParseOptionalTimestamp(string? value, string fieldName, string operation)
    {
        if (value is null)
            return null;
        if (!DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            throw InvalidResponse(operation, $"{fieldName} must be an RFC 3339 timestamp");
        }

        return timestamp;
    }

    private sealed class CreateRequestBody
    {
        [JsonPropertyName("service_slug")]
        public string ServiceSlug { get; init; } = string.Empty;

        [JsonPropertyName("label")]
        public string? Label { get; init; }

        [JsonPropertyName("requested_by")]
        public string? RequestedBy { get; init; }

        [JsonPropertyName("callback_url")]
        public string? CallbackUrl { get; init; }

        [JsonPropertyName("expires_in")]
        public long? ExpiresIn { get; init; }
    }

    private sealed class CreateResponseBody
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("connect_url")]
        public string? ConnectUrl { get; init; }

        [JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; init; }
    }

    private sealed class StatusResponseBody
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("service_slug")]
        public string? ServiceSlug { get; init; }

        [JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; init; }

        [JsonPropertyName("completed_at")]
        public string? CompletedAt { get; init; }

        [JsonPropertyName("connected_service")]
        public ConnectedServiceBody? ConnectedService { get; init; }
    }

    private sealed class ConnectedServiceBody
    {
        // NyxID's status response names this field `id`; its source is
        // ConnectLink.completed_user_service_id, so expose it as UserServiceId.
        [JsonPropertyName("id")]
        public string? Id { get; init; }
    }
}
