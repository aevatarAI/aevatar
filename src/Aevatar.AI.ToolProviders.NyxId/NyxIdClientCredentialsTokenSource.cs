using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class NyxIdClientCredentialsTokenSource : INyxIdClientCredentialsTokenSource
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    private readonly NyxIdToolOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TokenSnapshot? _cachedToken;
    private string? _cachedTokenEndpoint;

    public NyxIdClientCredentialsTokenSource(
        NyxIdToolOptions options,
        HttpClient httpClient,
        ILogger<NyxIdClientCredentialsTokenSource>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? NullLogger<NyxIdClientCredentialsTokenSource>.Instance;
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId) ||
            string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (_cachedToken is { } cachedToken && cachedToken.ExpiresAt > now.Add(ExpirySkew))
            return cachedToken.AccessToken;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cachedToken is { } freshCachedToken && freshCachedToken.ExpiresAt > now.Add(ExpirySkew))
                return freshCachedToken.AccessToken;

            var tokenEndpoint = await ResolveTokenEndpointAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(tokenEndpoint))
                return null;

            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new FormUrlEncodedContent(BuildTokenRequestPairs());

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "NyxID client-credentials token request failed with status {Status}",
                    (int)response.StatusCode);
                return null;
            }

            var token = ParseTokenResponse(body, now);
            if (token is null)
            {
                _logger.LogWarning("NyxID client-credentials token response is invalid");
                return null;
            }

            _cachedToken = token;
            return token.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private IEnumerable<KeyValuePair<string, string>> BuildTokenRequestPairs()
    {
        yield return new KeyValuePair<string, string>("grant_type", "client_credentials");
        yield return new KeyValuePair<string, string>("client_id", _options.ClientId!.Trim());
        yield return new KeyValuePair<string, string>("client_secret", _options.ClientSecret!.Trim());
        if (!string.IsNullOrWhiteSpace(_options.ClientCredentialsScope))
            yield return new KeyValuePair<string, string>("scope", _options.ClientCredentialsScope.Trim());
    }

    private async Task<string?> ResolveTokenEndpointAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_cachedTokenEndpoint))
            return _cachedTokenEndpoint;

        var authority = _options.EffectiveAuthority?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(authority))
            return null;

        using var response = await _httpClient.GetAsync(
            $"{authority}/.well-known/openid-configuration",
            ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "NyxID OIDC discovery failed with status {Status}",
                (int)response.StatusCode);
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("token_endpoint", out var tokenEndpoint) ||
                tokenEndpoint.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(tokenEndpoint.GetString()))
            {
                return null;
            }

            _cachedTokenEndpoint = tokenEndpoint.GetString()!.Trim();
            return _cachedTokenEndpoint;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "NyxID OIDC discovery response is invalid JSON");
            return null;
        }
    }

    private static TokenSnapshot? ParseTokenResponse(string body, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("access_token", out var accessTokenProperty) ||
                accessTokenProperty.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(accessTokenProperty.GetString()))
            {
                return null;
            }

            var expiresIn = root.TryGetProperty("expires_in", out var expiresInProperty) &&
                            expiresInProperty.ValueKind == JsonValueKind.Number &&
                            expiresInProperty.TryGetInt64(out var seconds) &&
                            seconds > 0
                ? seconds
                : 300;
            return new TokenSnapshot(
                accessTokenProperty.GetString()!.Trim(),
                now.AddSeconds(expiresIn));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record TokenSnapshot(string AccessToken, DateTimeOffset ExpiresAt);
}
