using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.GAgents.NyxidChat;

internal sealed record NyxRelayJwtValidationResult(
    bool Succeeded,
    ClaimsPrincipal? Principal = null,
    string? Subject = null,
    string? RelayApiKeyId = null,
    string? Error = null);

internal sealed class NyxRelayJwtValidator
{
    private sealed record CachedOidcConfiguration(
        string Issuer,
        string Audience,
        string JwksUri,
        IReadOnlyList<SecurityKey> SigningKeys,
        DateTimeOffset ExpiresAtUtc);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NyxIdToolOptions _nyxOptions;
    private readonly NyxIdRelayOptions _relayOptions;
    private readonly ILogger<NyxRelayJwtValidator> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CachedOidcConfiguration? _cachedConfiguration;

    public NyxRelayJwtValidator(
        IHttpClientFactory httpClientFactory,
        NyxIdToolOptions nyxOptions,
        NyxIdRelayOptions relayOptions,
        ILogger<NyxRelayJwtValidator>? logger = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _nyxOptions = nyxOptions ?? throw new ArgumentNullException(nameof(nyxOptions));
        _relayOptions = relayOptions ?? throw new ArgumentNullException(nameof(relayOptions));
        _logger = logger ?? NullLogger<NyxRelayJwtValidator>.Instance;
    }

    public async Task<NyxRelayJwtValidationResult> ValidateAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new NyxRelayJwtValidationResult(false, Error: "missing_token");

        var configuration = await GetConfigurationAsync(forceRefresh: false, ct);
        var initial = ValidateCore(token, configuration);
        if (initial.Succeeded)
            return initial;

        if (!RequiresKeyRefresh(initial.Error))
            return initial;

        _logger.LogInformation("Refreshing Nyx OIDC/JWKS cache after key-miss validation failure");
        var refreshed = await GetConfigurationAsync(forceRefresh: true, ct);
        return ValidateCore(token, refreshed);
    }

    private NyxRelayJwtValidationResult ValidateCore(string token, CachedOidcConfiguration configuration)
    {
        var handler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false,
        };
        var parameters = new TokenValidationParameters
        {
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidAlgorithms =
            [
                SecurityAlgorithms.RsaSha256,
                SecurityAlgorithms.EcdsaSha256,
            ],
            ValidateIssuer = true,
            ValidIssuer = configuration.Issuer,
            ValidateAudience = true,
            ValidAudience = configuration.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(Math.Max(0, _relayOptions.JwtClockSkewSeconds)),
        };

        try
        {
            var principal = handler.ValidateToken(token, parameters, out _);
            var subject = principal.FindFirstValue("sub")?.Trim();
            var relayApiKeyId = principal.FindFirstValue("relay_api_key_id")?.Trim();
            if (string.IsNullOrWhiteSpace(subject))
                return new NyxRelayJwtValidationResult(false, Error: "jwt_missing_sub");
            if (string.IsNullOrWhiteSpace(relayApiKeyId))
                return new NyxRelayJwtValidationResult(false, Error: "jwt_missing_relay_api_key_id");

            return new NyxRelayJwtValidationResult(
                true,
                Principal: principal,
                Subject: subject,
                RelayApiKeyId: relayApiKeyId);
        }
        catch (SecurityTokenSignatureKeyNotFoundException ex)
        {
            _logger.LogWarning(ex, "Nyx relay JWT validation failed: signing key not found");
            return new NyxRelayJwtValidationResult(false, Error: "signing_key_not_found");
        }
        catch (SecurityTokenExpiredException ex)
        {
            _logger.LogWarning(ex, "Nyx relay JWT validation failed: token expired");
            return new NyxRelayJwtValidationResult(false, Error: "token_expired");
        }
        catch (SecurityTokenInvalidAudienceException ex)
        {
            _logger.LogWarning(ex, "Nyx relay JWT validation failed: invalid audience");
            return new NyxRelayJwtValidationResult(false, Error: "invalid_audience");
        }
        catch (SecurityTokenInvalidIssuerException ex)
        {
            _logger.LogWarning(ex, "Nyx relay JWT validation failed: invalid issuer");
            return new NyxRelayJwtValidationResult(false, Error: "invalid_issuer");
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "Nyx relay JWT validation failed: security token error");
            return new NyxRelayJwtValidationResult(false, Error: ex.GetType().Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nyx relay JWT validation failed unexpectedly");
            return new NyxRelayJwtValidationResult(false, Error: ex.GetType().Name);
        }
    }

    private static bool RequiresKeyRefresh(string? error) =>
        string.Equals(error, "signing_key_not_found", StringComparison.Ordinal);

    private async Task<CachedOidcConfiguration> GetConfigurationAsync(bool forceRefresh, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cached = _cachedConfiguration;
        if (!forceRefresh && cached is not null && cached.ExpiresAtUtc > now)
            return cached;

        await _refreshLock.WaitAsync(ct);
        try
        {
            cached = _cachedConfiguration;
            if (!forceRefresh && cached is not null && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
                return cached;

            var discoveryUrl = ResolveDiscoveryUrl();
            var http = _httpClientFactory.CreateClient();

            using var discoveryResponse = await http.GetAsync(discoveryUrl, ct);
            discoveryResponse.EnsureSuccessStatusCode();
            var discoveryJson = await discoveryResponse.Content.ReadAsStringAsync(ct);

            using var discoveryDocument = JsonDocument.Parse(discoveryJson);
            var issuer = RequireString(discoveryDocument.RootElement, "issuer");
            var jwksUri = RequireString(discoveryDocument.RootElement, "jwks_uri");
            var audience = ResolveExpectedAudience();

            using var jwksResponse = await http.GetAsync(jwksUri, ct);
            jwksResponse.EnsureSuccessStatusCode();
            var jwksJson = await jwksResponse.Content.ReadAsStringAsync(ct);
            var signingKeys = new JsonWebKeySet(jwksJson).GetSigningKeys().ToArray();

            var configuration = new CachedOidcConfiguration(
                Issuer: issuer,
                Audience: audience,
                JwksUri: jwksUri,
                SigningKeys: signingKeys,
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, _relayOptions.OidcCacheTtlSeconds)));

            _cachedConfiguration = configuration;
            return configuration;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private string ResolveDiscoveryUrl()
    {
        if (!string.IsNullOrWhiteSpace(_relayOptions.OidcDiscoveryUrl))
            return _relayOptions.OidcDiscoveryUrl.Trim();

        var baseUrl = _nyxOptions.BaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("NyxID base URL is not configured.");

        return $"{baseUrl}/.well-known/openid-configuration";
    }

    private string ResolveExpectedAudience()
    {
        if (!string.IsNullOrWhiteSpace(_relayOptions.ExpectedAudience))
            return _relayOptions.ExpectedAudience.Trim();

        var baseUrl = _nyxOptions.BaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("NyxID base URL is not configured.");

        return baseUrl;
    }

    private static string RequireString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"Nyx OIDC discovery document is missing required string property '{propertyName}'.");
        }

        return property.GetString() ?? throw new InvalidOperationException(
            $"Nyx OIDC discovery property '{propertyName}' is null.");
    }
}
