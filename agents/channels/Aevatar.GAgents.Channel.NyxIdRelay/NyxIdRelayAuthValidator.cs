using System.Globalization;
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
    string? UserAccessToken = null,
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
    private readonly INyxIdRelayRegistrationCredentialResolver? _registrationCredentialResolver;
    private readonly INyxIdRelayReplayGuard? _replayGuard;
    private readonly ILogger<NyxIdRelayAuthValidator> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CachedOidcConfiguration? _cachedConfiguration;

    public NyxIdRelayAuthValidator(
        IHttpClientFactory httpClientFactory,
        NyxIdToolOptions nyxOptions,
        NyxIdRelayOptions relayOptions,
        ILogger<NyxIdRelayAuthValidator>? logger = null,
        INyxIdRelayRegistrationCredentialResolver? registrationCredentialResolver = null,
        INyxIdRelayReplayGuard? replayGuard = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _nyxOptions = nyxOptions ?? throw new ArgumentNullException(nameof(nyxOptions));
        _relayOptions = relayOptions ?? throw new ArgumentNullException(nameof(relayOptions));
        _registrationCredentialResolver = registrationCredentialResolver;
        _replayGuard = replayGuard;
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
            return jwtValidation with { UserAccessToken = null };

        if (!string.IsNullOrWhiteSpace(payload.Agent?.ApiKeyId) &&
            !string.Equals(payload.Agent.ApiKeyId.Trim(), jwtValidation.RelayApiKeyId, StringComparison.Ordinal))
        {
            return new NyxIdRelayAuthenticationResult(
                false,
                ErrorCode: "relay_api_key_mismatch",
                ErrorSummary: "Relay callback agent api_key_id does not match validated relay token.");
        }

        var headerMessageId = NormalizeOptional(http.Request.Headers["X-NyxID-Message-Id"].FirstOrDefault());
        if (_relayOptions.RequireMessageIdHeader && headerMessageId is null)
        {
            return new NyxIdRelayAuthenticationResult(
                false,
                ErrorCode: "missing_message_id_header",
                ErrorSummary: "Relay callback is missing X-NyxID-Message-Id.");
        }

        if (headerMessageId is not null &&
            !string.Equals(headerMessageId, NormalizeOptional(payload.MessageId), StringComparison.Ordinal))
        {
            return new NyxIdRelayAuthenticationResult(
                false,
                ErrorCode: "message_id_mismatch",
                ErrorSummary: "Relay callback header message id does not match payload message_id.");
        }

        if (!TryValidateTimestamp(http, out var observedAtUtc, out var timestampErrorCode, out var timestampErrorSummary))
        {
            return new NyxIdRelayAuthenticationResult(
                false,
                ErrorCode: timestampErrorCode,
                ErrorSummary: timestampErrorSummary);
        }

        NyxIdRelayRegistrationCredential? registrationCredential = null;
        try
        {
            registrationCredential = await ResolveRegistrationCredentialAsync(jwtValidation.RelayApiKeyId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Nyx relay registration credential resolution failed for relay_api_key_id={RelayApiKeyId}",
                jwtValidation.RelayApiKeyId);
            return new NyxIdRelayAuthenticationResult(
                false,
                ErrorCode: "registration_credential_resolution_failed",
                ErrorSummary: "Relay callback registration credential lookup failed.");
        }

        if (!ValidateSignature(http, bodyBytes, registrationCredential))
        {
            return new NyxIdRelayAuthenticationResult(
                false,
                ErrorCode: "invalid_signature",
                ErrorSummary: "Relay callback signature verification failed.");
        }

        if (headerMessageId is not null &&
            _replayGuard is not null &&
            !_replayGuard.TryClaim(headerMessageId, observedAtUtc))
        {
            return new NyxIdRelayAuthenticationResult(
                false,
                ErrorCode: "replay_detected",
                ErrorSummary: "Relay callback replay was rejected.");
        }

        return jwtValidation with { UserAccessToken = userToken };
    }

    public bool ValidateSignature(
        HttpContext http,
        byte[] bodyBytes,
        NyxIdRelayRegistrationCredential? registrationCredential = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(bodyBytes);

        if (_relayOptions.SkipSignatureVerification)
            return true;

        var signingSecret = NormalizeOptional(registrationCredential?.RelayApiKeyHash)
                            ?? NormalizeOptional(_relayOptions.HmacSecret);
        if (signingSecret is null)
            return false;

        if (!http.Request.Headers.TryGetValue("X-NyxID-Signature", out var signatureHeader) ||
            string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
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

            EnsureCanonicalScopeClaim(principal, scopeId);

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

    private static void EnsureCanonicalScopeClaim(ClaimsPrincipal principal, string scopeId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);

        if (principal.Claims.Any(claim =>
                string.Equals(claim.Type, "scope_id", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(claim.Value?.Trim(), scopeId, StringComparison.Ordinal)))
        {
            return;
        }

        var identity = principal.Identities.FirstOrDefault(candidate => candidate.IsAuthenticated)
            ?? principal.Identities.FirstOrDefault();
        identity?.AddClaim(new Claim("scope_id", scopeId));
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

    private async Task<NyxIdRelayRegistrationCredential?> ResolveRegistrationCredentialAsync(
        string? relayApiKeyId,
        CancellationToken ct)
    {
        var normalizedRelayApiKeyId = NormalizeOptional(relayApiKeyId);
        if (normalizedRelayApiKeyId is null || _registrationCredentialResolver is null)
            return null;

        return await _registrationCredentialResolver.ResolveAsync(normalizedRelayApiKeyId, ct);
    }

    private bool TryValidateTimestamp(
        HttpContext http,
        out DateTimeOffset observedAtUtc,
        out string errorCode,
        out string errorSummary)
    {
        observedAtUtc = DateTimeOffset.UtcNow;
        errorCode = string.Empty;
        errorSummary = string.Empty;

        var headerTimestamp = NormalizeOptional(http.Request.Headers["X-NyxID-Timestamp"].FirstOrDefault());
        if (headerTimestamp is null)
        {
            if (_relayOptions.RequireTimestampHeader)
            {
                errorCode = "missing_timestamp_header";
                errorSummary = "Relay callback is missing X-NyxID-Timestamp.";
                return false;
            }

            return true;
        }

        if (!DateTimeOffset.TryParse(
                headerTimestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out observedAtUtc))
        {
            errorCode = "invalid_timestamp_header";
            errorSummary = "Relay callback X-NyxID-Timestamp is not a valid ISO 8601 timestamp.";
            return false;
        }

        var replayWindow = TimeSpan.FromSeconds(Math.Max(1, _relayOptions.ReplayWindowSeconds));
        var now = DateTimeOffset.UtcNow;
        if ((now - observedAtUtc).Duration() > replayWindow)
        {
            errorCode = "timestamp_outside_replay_window";
            errorSummary = "Relay callback timestamp is outside the allowed replay window.";
            return false;
        }

        return true;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
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
