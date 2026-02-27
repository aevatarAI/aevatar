using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.MCP;

/// <summary>
/// DelegatingHandler that automatically acquires and refreshes an OAuth2
/// client_credentials token before each outgoing HTTP request.
/// </summary>
internal sealed class OAuthTokenHandler : DelegatingHandler
{
    private readonly MCPAuthConfig _auth;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public OAuthTokenHandler(MCPAuthConfig auth, ILogger logger)
        : base(new HttpClientHandler())
    {
        _auth = auth;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _cachedToken;

        await _semaphore.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the lock
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _cachedToken;

            using var tokenClient = new HttpClient();
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _auth.ClientId,
                ["client_secret"] = _auth.ClientSecret,
            };
            if (!string.IsNullOrWhiteSpace(_auth.Scope))
                form["scope"] = _auth.Scope;

            using var response = await tokenClient.PostAsync(
                _auth.TokenUrl, new FormUrlEncodedContent(form), ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                var truncated = body.Length > 200 ? body[..200] + "..." : body;
                throw new InvalidOperationException(
                    $"OAuth token fetch failed ({response.StatusCode}): {truncated}");
            }

            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            _cachedToken = json.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("OAuth response missing access_token");

            // Use expires_in if available, default to 5 minutes, subtract 30s margin
            var expiresIn = json.RootElement.TryGetProperty("expires_in", out var exp)
                ? exp.GetInt32()
                : 300;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(expiresIn - 30, 10));

            _logger.LogDebug("MCP OAuth token refreshed, expires at {ExpiresAt}", _expiresAt);
            return _cachedToken;
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
