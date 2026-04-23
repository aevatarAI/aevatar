using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed record NyxIdRelayAuthenticationResult(
    bool Succeeded,
    ClaimsPrincipal? Principal = null,
    string? ScopeId = null,
    string? RelayApiKeyId = null,
    string? ReplyAccessToken = null,
    string? ErrorCode = null,
    string? ErrorSummary = null);

public sealed class NyxIdRelayAuthValidator
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
    private readonly ILogger<NyxIdRelayAuthValidator> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CachedOidcConfiguration? _cachedConfiguration;

    public NyxIdRelayAuthValidator(
        IHttpClientFactory httpClientFactory,
        NyxIdToolOptions nyxOptions,
        NyxIdRelayOptions relayOptions,
        ILogger<NyxIdRelayAuthValidator>? logger = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _nyxOptions = nyxOptions ?? throw new ArgumentNullException(nameof(nyxOptions));
        _relayOptions = relayOptions ?? throw new ArgumentNullException(nameof(relayOptions));
        _logger = logger ?? NullLogger<NyxIdRelayAuthValidator>.Instance;
    }

    public async Task<NyxIdRelayAuthenticationResult> ValidateAsync(
        HttpContext http,
        byte[] bodyBytes,
        NyxIdRelayCallbackPayload payload,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(bodyBytes);
        ArgumentNullException.ThrowIfNull(payload);

        var userToken = http.Request.Headers["X-NyxID-User-Token"].FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(userToken))
        {
            return new NyxIdRelayAuthenticationResult(
                false,
                ErrorCode: "missing_user_token",
                ErrorSummary: "Relay callback is missing X-NyxID-User-Token.");
        }

        var jwtValidation = await ValidateJwtAsync(userToken, ct);
        if (!jwtValidation.Succeeded)
            return jwtValidation with { ReplyAccessToken = null };

        if (!string.IsNullOrWhiteSpace(payload.Agent?.ApiKeyId) &&
            !string.Equals(payload.Agent.ApiKeyId.Trim(), jwtValidation.RelayApiKeyId, StringComparison.Ordinal))
        {
            return new NyxIdRelayAuthenticationResult(
                false,
                ErrorCode: "relay_api_key_mismatch",
                ErrorSummary: "Relay callback agent api_key_id does not match validated relay token.");
        }

        if (!ValidateSignature(http, bodyBytes))
        {
            return new NyxIdRelayAuthenticationResult(
                false,
                ErrorCode: "invalid_signature",
                ErrorSummary: "Relay callback signature verification failed.");
        }

        var headerMessageId = http.Request.Headers["X-NyxID-Message-Id"].FirstOrDefault()?.Trim();
        if (_relayOptions.RequireMessageIdHeader && string.IsNullOrWhiteSpace(headerMessageId))
        {
            return new NyxIdRelayAuthenticationResult(
                false,
                ErrorCode: "missing_message_id_header",
                ErrorSummary: "Relay callback is missing X-NyxID-Message-Id.");
        }

        if (!string.IsNullOrWhiteSpace(headerMessageId) &&
            !string.Equals(headerMessageId, payload.MessageId?.Trim(), StringComparison.Ordinal))
        {
            return new NyxIdRelayAuthenticationResult(
                false,
                ErrorCode: "message_id_mismatch",
                ErrorSummary: "Relay callback header message id does not match payload message_id.");
        }

        return jwtValidation with { ReplyAccessToken = userToken };
    }

    public bool ValidateSignature(HttpContext http, byte[] bodyBytes)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(bodyBytes);

        if (_relayOptions.SkipSignatureVerification)
            return true;

        if (string.IsNullOrWhiteSpace(_relayOptions.HmacSecret))
            return false;

        if (!http.Request.Headers.TryGetValue("X-NyxID-Signature", out var signatureHeader) ||
            string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_relayOptions.HmacSecret));
        var computedHash = hmac.ComputeHash(bodyBytes);
        var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedSignature),
            Encoding.UTF8.GetBytes(signatureHeader.ToString().Trim().ToLowerInvariant()));
    }

    private async Task<NyxIdRelayAuthenticationResult> ValidateJwtAsync(string token, CancellationToken ct)
    {
        var configuration = await GetConfigurationAsync(forceRefresh: false, ct);
        var initial = ValidateJwtCore(token, configuration);
        if (initial.Succeeded)
            return initial;

        if (!string.Equals(initial.ErrorCode, "signing_key_not_found", StringComparison.Ordinal))
            return initial;

        _logger.LogInformation("Refreshing Nyx relay OIDC/JWKS cache after signing key miss");
        var refreshed = await GetConfigurationAsync(forceRefresh: true, ct);
        return ValidateJwtCore(token, refreshed);
    }

    private NyxIdRelayAuthenticationResult ValidateJwtCore(string token, CachedOidcConfiguration configuration)
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
            var scopeId = principal.FindFirstValue("sub")?.Trim();
            var relayApiKeyId = principal.FindFirstValue("relay_api_key_id")?.Trim();
            if (string.IsNullOrWhiteSpace(scopeId))
            {
                return new NyxIdRelayAuthenticationResult(
                    false,
                    ErrorCode: "jwt_missing_sub",
                    ErrorSummary: "Validated relay JWT is missing subject.");
            }

            if (string.IsNullOrWhiteSpace(relayApiKeyId))
            {
                return new NyxIdRelayAuthenticationResult(
                    false,
                    ErrorCode: "jwt_missing_relay_api_key_id",
                    ErrorSummary: "Validated relay JWT is missing relay_api_key_id.");
            }

            return new NyxIdRelayAuthenticationResult(
                true,
                Principal: principal,
                ScopeId: scopeId,
                RelayApiKeyId: relayApiKeyId);
        }
        catch (SecurityTokenSignatureKeyNotFoundException ex)
        {
            _logger.LogWarning(ex, "Nyx relay JWT validation failed: signing key not found");
            return new NyxIdRelayAuthenticationResult(false, ErrorCode: "signing_key_not_found", ErrorSummary: ex.Message);
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "Nyx relay JWT validation failed");
            return new NyxIdRelayAuthenticationResult(false, ErrorCode: ex.GetType().Name, ErrorSummary: ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nyx relay JWT validation failed unexpectedly");
            return new NyxIdRelayAuthenticationResult(false, ErrorCode: ex.GetType().Name, ErrorSummary: ex.Message);
        }
    }

    private async Task<CachedOidcConfiguration> GetConfigurationAsync(bool forceRefresh, CancellationToken ct)
    {
        var cached = _cachedConfiguration;
        if (!forceRefresh && cached is not null && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
            return cached;

        await _refreshLock.WaitAsync(ct);
        try
        {
            cached = _cachedConfiguration;
            if (!forceRefresh && cached is not null && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
                return cached;

            var http = _httpClientFactory.CreateClient();
            using var discoveryResponse = await http.GetAsync(ResolveDiscoveryUrl(), ct);
            discoveryResponse.EnsureSuccessStatusCode();
            var discoveryJson = await discoveryResponse.Content.ReadAsStringAsync(ct);
            using var discoveryDocument = JsonDocument.Parse(discoveryJson);

            var issuer = RequireString(discoveryDocument.RootElement, "issuer");
            var jwksUri = RequireString(discoveryDocument.RootElement, "jwks_uri");

            using var jwksResponse = await http.GetAsync(jwksUri, ct);
            jwksResponse.EnsureSuccessStatusCode();
            var jwksJson = await jwksResponse.Content.ReadAsStringAsync(ct);

            var configuration = new CachedOidcConfiguration(
                Issuer: issuer,
                Audience: ResolveExpectedAudience(),
                JwksUri: jwksUri,
                SigningKeys: new JsonWebKeySet(jwksJson).GetSigningKeys().ToArray(),
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
                $"Nyx relay OIDC discovery document is missing required string property '{propertyName}'.");
        }

        return property.GetString() ?? throw new InvalidOperationException(
            $"Nyx relay OIDC discovery property '{propertyName}' is null.");
    }
}
