using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Sisyphus.Application.Services;

public sealed class NyxIdOptions
{
    public const string SectionName = "NyxId";

    public required string BaseUrl { get; set; }
    public required string TokenUrl { get; set; }
    public required string ClientId { get; set; }
    public required string ClientSecret { get; set; }
    /// <summary>NyxId downstream service UUID for chrono-graph.</summary>
    public required string ChronoGraphServiceId { get; set; }
}

public sealed class NyxIdTokenService(HttpClient httpClient, IOptions<NyxIdOptions> options)
{
    private readonly NyxIdOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cachedToken;
    private DateTime _expiresAt = DateTime.MinValue;

    public async Task SetAuthHeaderAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && DateTime.UtcNow < _expiresAt)
            return _cachedToken;

        await _lock.WaitAsync(ct);
        try
        {
            // Double-check after lock
            if (_cachedToken is not null && DateTime.UtcNow < _expiresAt)
                return _cachedToken;

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
            });

            var response = await httpClient.PostAsync(_options.TokenUrl, content, ct);
            response.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            _cachedToken = doc.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Token response missing access_token");

            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp)
                ? exp.GetInt32()
                : 3600;
            // Refresh 5 minutes before expiry
            _expiresAt = DateTime.UtcNow.AddSeconds(expiresIn - 300);

            return _cachedToken;
        }
        finally
        {
            _lock.Release();
        }
    }
}
