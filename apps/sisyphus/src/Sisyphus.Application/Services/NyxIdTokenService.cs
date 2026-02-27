using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Sisyphus.Application.Services;

/// <summary>
/// Acquires and caches OAuth2 client_credentials tokens from NyxId.
/// </summary>
public sealed class NyxIdTokenService(
    IConfiguration configuration,
    ILogger<NyxIdTokenService> logger)
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _cachedToken;

        await _semaphore.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the lock.
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _cachedToken;

            var tokenUrl = configuration["NYXID_AUTH_TOKEN_URL"]
                ?? throw new InvalidOperationException("NYXID_AUTH_TOKEN_URL is not configured");
            var clientId = configuration["NYXID_SA_CLIENT_ID"]
                ?? throw new InvalidOperationException("NYXID_SA_CLIENT_ID is not configured");
            var clientSecret = configuration["NYXID_SA_CLIENT_SECRET"]
                ?? throw new InvalidOperationException("NYXID_SA_CLIENT_SECRET is not configured");

            using var httpClient = new HttpClient();
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            });

            var response = await httpClient.PostAsync(tokenUrl, content, ct);
            response.EnsureSuccessStatusCode();

            var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(ct)
                ?? throw new InvalidOperationException("Token endpoint returned null");

            _cachedToken = tokenResponse.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn - 30);

            logger.LogDebug("Acquired NyxId access token, expires at {ExpiresAt}", _expiresAt);
            return _cachedToken;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
