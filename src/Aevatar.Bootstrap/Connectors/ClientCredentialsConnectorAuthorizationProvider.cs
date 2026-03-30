using System.Net.Http.Headers;
using System.Text.Json;
using Aevatar.Configuration;

namespace Aevatar.Bootstrap.Connectors;

public sealed class ClientCredentialsConnectorAuthorizationProvider : IConnectorRequestAuthorizationProvider
{
    private readonly ConnectorAuthConfig _authConfig;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly string _httpClientName;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private AccessTokenSnapshot? _cachedToken;

    public ClientCredentialsConnectorAuthorizationProvider(
        ConnectorAuthConfig authConfig,
        IHttpClientFactory? httpClientFactory = null,
        string? httpClientName = null)
    {
        _authConfig = authConfig ?? throw new ArgumentNullException(nameof(authConfig));
        _httpClientFactory = httpClientFactory;
        _httpClientName = string.IsNullOrWhiteSpace(httpClientName) ? "default" : httpClientName.Trim();
    }

    public async Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        if (request.Headers.Authorization != null || !IsConfigured(_authConfig))
            return;

        var token = await GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue(token.TokenType, token.AccessToken);
    }

    private async Task<AccessTokenSnapshot> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_cachedToken is { } cached && !cached.IsExpired)
            return cached;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedToken is { } refreshed && !refreshed.IsExpired)
                return refreshed;

            using var request = new HttpRequestMessage(HttpMethod.Post, _authConfig.TokenUrl)
            {
                Content = new FormUrlEncodedContent(BuildTokenRequestPairs()),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await ResolveHttpClient().SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"client_credentials token request failed: {(int)response.StatusCode} {response.ReasonPhrase}".Trim());

            _cachedToken = ParseTokenResponse(body);
            return _cachedToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private IEnumerable<KeyValuePair<string, string>> BuildTokenRequestPairs()
    {
        yield return new KeyValuePair<string, string>("grant_type", "client_credentials");
        yield return new KeyValuePair<string, string>("client_id", _authConfig.ClientId);
        yield return new KeyValuePair<string, string>("client_secret", _authConfig.ClientSecret);
        if (!string.IsNullOrWhiteSpace(_authConfig.Scope))
            yield return new KeyValuePair<string, string>("scope", _authConfig.Scope);
    }

    private AccessTokenSnapshot ParseTokenResponse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!root.TryGetProperty("access_token", out var accessTokenElement) ||
            accessTokenElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(accessTokenElement.GetString()))
        {
            throw new InvalidOperationException("client_credentials token response did not include access_token.");
        }

        var accessToken = accessTokenElement.GetString()!.Trim();
        var tokenType = root.TryGetProperty("token_type", out var tokenTypeElement) &&
                        tokenTypeElement.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(tokenTypeElement.GetString())
            ? tokenTypeElement.GetString()!.Trim()
            : "Bearer";
        var expiresInSeconds = root.TryGetProperty("expires_in", out var expiresInElement)
            ? ReadExpiresInSeconds(expiresInElement)
            : 300;
        var validFor = Math.Max(30, expiresInSeconds - 60);
        return new AccessTokenSnapshot(accessToken, tokenType, DateTimeOffset.UtcNow.AddSeconds(validFor));
    }

    private static int ReadExpiresInSeconds(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var numericValue))
            return numericValue;

        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), out var stringValue))
            return stringValue;

        return 300;
    }

    private HttpClient ResolveHttpClient() =>
        _httpClientFactory?.CreateClient(_httpClientName) ?? SharedHttpClient.Instance;

    public static bool IsConfigured(ConnectorAuthConfig authConfig) =>
        authConfig != null &&
        string.Equals(authConfig.Type, "client_credentials", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(authConfig.TokenUrl) &&
        !string.IsNullOrWhiteSpace(authConfig.ClientId) &&
        !string.IsNullOrWhiteSpace(authConfig.ClientSecret);

    private sealed record AccessTokenSnapshot(
        string AccessToken,
        string TokenType,
        DateTimeOffset ExpiresAtUtc)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAtUtc;
    }

    private static class SharedHttpClient
    {
        internal static readonly HttpClient Instance = new();
    }
}
