using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Hosting;

internal sealed class NyxIdAccessTokenHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly NyxIdInternalRequestCredentials _internalCredentials;
    private readonly NyxIdAppTokenService _tokenService;

    public NyxIdAccessTokenHandler(
        IHttpContextAccessor httpContextAccessor,
        NyxIdInternalRequestCredentials internalCredentials,
        NyxIdAppTokenService tokenService)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _internalCredentials = internalCredentials ?? throw new ArgumentNullException(nameof(internalCredentials));
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var httpContext = _httpContextAccessor.HttpContext;
        if (ShouldAttachInternalAuthHeader(request, httpContext) &&
            !request.Headers.Contains(NyxIdInternalRequestCredentials.HeaderName))
        {
            request.Headers.TryAddWithoutValidation(
                NyxIdInternalRequestCredentials.HeaderName,
                _internalCredentials.Token);
        }

        if (request.Headers.Authorization == null)
        {
            var accessToken = await _tokenService.GetAccessTokenAsync(
                httpContext,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(accessToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static bool ShouldAttachInternalAuthHeader(HttpRequestMessage request, HttpContext? httpContext)
    {
        var requestUri = request.RequestUri;
        if (requestUri == null || !requestUri.IsLoopback)
            return false;

        var currentPort = httpContext?.Request.Host.Port;
        return currentPort.HasValue && currentPort.Value == requestUri.Port;
    }
}

internal sealed class NyxIdAppTokenService
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<NyxIdAppAuthOptions> _options;
    private readonly ILogger<NyxIdAppTokenService> _logger;

    public NyxIdAppTokenService(
        IHttpClientFactory httpClientFactory,
        IOptions<NyxIdAppAuthOptions> options,
        ILogger<NyxIdAppTokenService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> GetAccessTokenAsync(HttpContext? httpContext, CancellationToken cancellationToken)
    {
        if (httpContext?.User?.Identity?.IsAuthenticated != true)
            return null;

        var result = await httpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal == null || result.Properties == null)
            return null;

        var accessToken = result.Properties.GetTokenValue("access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        if (!ShouldRefresh(result.Properties))
            return accessToken;

        var refreshToken = result.Properties.GetTokenValue("refresh_token");
        if (string.IsNullOrWhiteSpace(refreshToken))
            return accessToken;

        var refreshed = await RefreshAsync(refreshToken, cancellationToken);
        if (refreshed == null)
        {
            if (IsExpired(result.Properties))
            {
                _logger.LogWarning("NyxID access token refresh failed after token expiry; clearing local app session.");
                await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return null;
            }

            return accessToken;
        }

        PersistTokens(result.Properties, refreshed, result.Properties.GetTokenValue("id_token"));
        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            result.Principal,
            result.Properties);

        return refreshed.AccessToken;
    }

    private async Task<NyxIdRefreshTokenResponse?> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = _options.Value;
            var tokenEndpoint = string.IsNullOrWhiteSpace(options.TokenEndpoint)
                ? $"{options.Authority.TrimEnd('/')}/oauth/token"
                : options.TokenEndpoint.Trim();
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(BuildRefreshForm(refreshToken, options)),
            };

            var httpClient = _httpClientFactory.CreateClient();
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "NyxID refresh token request failed with status code {StatusCode}.",
                    (int)response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<NyxIdRefreshTokenResponse>(cancellationToken);
            if (payload == null || string.IsNullOrWhiteSpace(payload.AccessToken))
            {
                _logger.LogWarning("NyxID refresh token response did not contain a usable access token.");
                return null;
            }

            return payload;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "NyxID refresh token request failed.");
            return null;
        }
    }

    private static Dictionary<string, string> BuildRefreshForm(string refreshToken, NyxIdAppAuthOptions options)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = options.ClientId,
        };

        if (!string.IsNullOrWhiteSpace(options.ClientSecret))
            form["client_secret"] = options.ClientSecret;

        return form;
    }

    private static void PersistTokens(
        AuthenticationProperties properties,
        NyxIdRefreshTokenResponse refreshed,
        string? idToken)
    {
        var refreshToken = refreshed.RefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
            refreshToken = properties.GetTokenValue("refresh_token");

        var expiresAt = DateTimeOffset.UtcNow
            .AddSeconds(refreshed.ExpiresIn <= 0 ? 900 : refreshed.ExpiresIn)
            .ToString("o", CultureInfo.InvariantCulture);

        var tokens = new List<AuthenticationToken>
        {
            new() { Name = "access_token", Value = refreshed.AccessToken },
            new() { Name = "refresh_token", Value = refreshToken ?? string.Empty },
            new() { Name = "token_type", Value = refreshed.TokenType ?? "Bearer" },
            new() { Name = "expires_at", Value = expiresAt },
        };

        if (!string.IsNullOrWhiteSpace(idToken))
            tokens.Add(new AuthenticationToken { Name = "id_token", Value = idToken });

        properties.StoreTokens(tokens);
    }

    private static bool ShouldRefresh(AuthenticationProperties properties)
    {
        if (!TryGetExpiry(properties, out var expiresAt))
            return false;

        return expiresAt <= DateTimeOffset.UtcNow.Add(RefreshSkew);
    }

    private static bool IsExpired(AuthenticationProperties properties)
    {
        if (!TryGetExpiry(properties, out var expiresAt))
            return false;

        return expiresAt <= DateTimeOffset.UtcNow;
    }

    private static bool TryGetExpiry(AuthenticationProperties properties, out DateTimeOffset expiresAt)
    {
        var raw = properties.GetTokenValue("expires_at");
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out expiresAt);
    }

    private sealed record NyxIdRefreshTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken);
}
