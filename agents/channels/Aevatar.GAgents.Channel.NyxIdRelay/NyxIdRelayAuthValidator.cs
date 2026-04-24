using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
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

    private sealed record CallbackJwtValidationResult(
        bool Succeeded,
        ClaimsPrincipal? Principal = null,
        string? ScopeId = null,
        string? RelayApiKeyId = null,
        string? MessageId = null,
        string? Platform = null,
        string? Jti = null,
        string? BodySha256 = null,
        string? ErrorCode = null,
        string? ErrorSummary = null);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NyxIdToolOptions _nyxOptions;
    private readonly NyxIdRelayOptions _relayOptions;
    private readonly INyxIdRelayReplayGuard? _replayGuard;
    private readonly ILogger<NyxIdRelayAuthValidator> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CachedOidcConfiguration? _cachedConfiguration;
    private DateTimeOffset _lastForcedRefreshUtc = DateTimeOffset.MinValue;

    public NyxIdRelayAuthValidator(
        IHttpClientFactory httpClientFactory,
        NyxIdToolOptions nyxOptions,
        NyxIdRelayOptions relayOptions,
        ILogger<NyxIdRelayAuthValidator>? logger = null,
        INyxIdRelayReplayGuard? replayGuard = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _nyxOptions = nyxOptions ?? throw new ArgumentNullException(nameof(nyxOptions));
        _relayOptions = relayOptions ?? throw new ArgumentNullException(nameof(relayOptions));
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

        var callbackToken = http.Request.Headers["X-NyxID-Callback-Token"].FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(callbackToken))
        {
            return Fail("callback_jwt_missing", "Relay callback is missing X-NyxID-Callback-Token.");
        }

        var jwtValidation = await ValidateCallbackJwtAsync(callbackToken, ct);
        if (!jwtValidation.Succeeded)
        {
            return Fail(jwtValidation.ErrorCode, jwtValidation.ErrorSummary);
        }

        var payloadApiKeyId = NormalizeOptional(payload.Agent?.ApiKeyId);
        if (payloadApiKeyId is null ||
            !string.Equals(payloadApiKeyId, jwtValidation.RelayApiKeyId, StringComparison.Ordinal))
        {
            return Fail(
                "callback_jwt_api_key_id_mismatch",
                "Relay callback agent api_key_id does not match callback JWT.");
        }

        var headerMessageId = NormalizeOptional(http.Request.Headers["X-NyxID-Message-Id"].FirstOrDefault());
        if (_relayOptions.RequireMessageIdHeader && headerMessageId is null)
        {
            return Fail("callback_jwt_message_id_mismatch", "Relay callback is missing X-NyxID-Message-Id.");
        }

        var payloadMessageId = NormalizeOptional(payload.MessageId);
        if (!string.Equals(jwtValidation.MessageId, payloadMessageId, StringComparison.Ordinal) ||
            (headerMessageId is not null && !string.Equals(headerMessageId, payloadMessageId, StringComparison.Ordinal)))
        {
            return Fail(
                "callback_jwt_message_id_mismatch",
                "Relay callback message id does not match callback JWT.");
        }

        var payloadPlatform = NormalizeOptional(payload.Platform);
        if (!string.Equals(jwtValidation.Platform, payloadPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                "callback_jwt_platform_mismatch",
                "Relay callback platform does not match callback JWT.");
        }

        var payloadCorrelationId = NormalizeOptional(payload.CorrelationId);
        if (payloadCorrelationId is null ||
            !string.Equals(payloadCorrelationId, jwtValidation.Jti, StringComparison.Ordinal))
        {
            return Fail(
                "callback_jwt_correlation_id_mismatch",
                "Relay callback correlation_id does not match callback JWT jti.");
        }

        if (!string.Equals(ComputeBodySha256Hex(bodyBytes), jwtValidation.BodySha256, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                "callback_jwt_body_hash_mismatch",
                "Relay callback raw body hash does not match callback JWT.");
        }

        if (_replayGuard is not null)
        {
            var observedAtUtc = DateTimeOffset.UtcNow;
            if (!_replayGuard.TryClaim($"jti:{jwtValidation.Jti}", observedAtUtc) ||
                !_replayGuard.TryClaim($"message:{payloadMessageId}", observedAtUtc))
            {
                return Fail("callback_jwt_replay_detected", "Relay callback replay was rejected.");
            }
        }

        var userToken = NormalizeOptional(http.Request.Headers["X-NyxID-User-Token"].FirstOrDefault());
        return new NyxIdRelayAuthenticationResult(
            true,
            Principal: jwtValidation.Principal,
            ScopeId: jwtValidation.ScopeId,
            RelayApiKeyId: jwtValidation.RelayApiKeyId,
            UserAccessToken: userToken);
    }

    private static NyxIdRelayAuthenticationResult Fail(string? code, string? summary)
    {
        var normalizedCode = string.IsNullOrWhiteSpace(code) ? "callback_jwt_invalid" : code.Trim();
        NyxIdRelayMetrics.RecordCallbackJwtValidationFailure(normalizedCode);
        return new NyxIdRelayAuthenticationResult(false, ErrorCode: normalizedCode, ErrorSummary: summary);
    }

    private async Task<CallbackJwtValidationResult> ValidateCallbackJwtAsync(string token, CancellationToken ct)
    {
        var configuration = await GetConfigurationAsync(forceRefresh: false, ct);
        var initial = ValidateCallbackJwtCore(token, configuration);
        if (initial.Succeeded)
            return initial;

        if (!string.Equals(initial.ErrorCode, "callback_jwt_kid_not_found", StringComparison.Ordinal))
            return initial;

        var refreshed = await RefreshConfigurationAfterKidMissAsync(ct);
        return ValidateCallbackJwtCore(token, refreshed);
    }

    private CallbackJwtValidationResult ValidateCallbackJwtCore(string token, CachedOidcConfiguration configuration)
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
            var relayApiKeyId = principal.FindFirstValue("api_key_id")?.Trim();
            var messageId = principal.FindFirstValue("message_id")?.Trim();
            var platform = principal.FindFirstValue("platform")?.Trim();
            var bodySha256 = principal.FindFirstValue("body_sha256")?.Trim();
            var jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti)?.Trim() ??
                      principal.FindFirstValue("jti")?.Trim();

            if (string.IsNullOrWhiteSpace(relayApiKeyId))
            {
                return new CallbackJwtValidationResult(
                    false,
                    ErrorCode: "callback_jwt_api_key_id_mismatch",
                    ErrorSummary: "Callback JWT is missing api_key_id.");
            }

            if (string.IsNullOrWhiteSpace(messageId))
            {
                return new CallbackJwtValidationResult(
                    false,
                    ErrorCode: "callback_jwt_message_id_mismatch",
                    ErrorSummary: "Callback JWT is missing message_id.");
            }

            if (string.IsNullOrWhiteSpace(platform))
            {
                return new CallbackJwtValidationResult(
                    false,
                    ErrorCode: "callback_jwt_platform_mismatch",
                    ErrorSummary: "Callback JWT is missing platform.");
            }

            if (string.IsNullOrWhiteSpace(jti))
            {
                return new CallbackJwtValidationResult(
                    false,
                    ErrorCode: "callback_jwt_correlation_id_mismatch",
                    ErrorSummary: "Callback JWT is missing jti.");
            }

            if (string.IsNullOrWhiteSpace(bodySha256))
            {
                return new CallbackJwtValidationResult(
                    false,
                    ErrorCode: "callback_jwt_body_hash_mismatch",
                    ErrorSummary: "Callback JWT is missing body_sha256.");
            }

            return new CallbackJwtValidationResult(
                true,
                Principal: principal,
                ScopeId: scopeId,
                RelayApiKeyId: relayApiKeyId,
                MessageId: messageId,
                Platform: platform,
                Jti: jti,
                BodySha256: bodySha256);
        }
        catch (SecurityTokenSignatureKeyNotFoundException ex)
        {
            _logger.LogWarning(ex, "Nyx relay callback JWT validation failed: signing key not found");
            return new CallbackJwtValidationResult(false, ErrorCode: "callback_jwt_kid_not_found", ErrorSummary: ex.Message);
        }
        catch (SecurityTokenInvalidIssuerException ex)
        {
            _logger.LogWarning(ex, "Nyx relay callback JWT issuer mismatch");
            return new CallbackJwtValidationResult(false, ErrorCode: "callback_jwt_issuer_mismatch", ErrorSummary: ex.Message);
        }
        catch (SecurityTokenInvalidAudienceException ex)
        {
            _logger.LogWarning(ex, "Nyx relay callback JWT audience mismatch");
            return new CallbackJwtValidationResult(false, ErrorCode: "callback_jwt_audience_mismatch", ErrorSummary: ex.Message);
        }
        catch (SecurityTokenExpiredException ex)
        {
            _logger.LogWarning(ex, "Nyx relay callback JWT lifetime invalid");
            return new CallbackJwtValidationResult(false, ErrorCode: "callback_jwt_lifetime_invalid", ErrorSummary: ex.Message);
        }
        catch (SecurityTokenNotYetValidException ex)
        {
            _logger.LogWarning(ex, "Nyx relay callback JWT lifetime invalid");
            return new CallbackJwtValidationResult(false, ErrorCode: "callback_jwt_lifetime_invalid", ErrorSummary: ex.Message);
        }
        catch (SecurityTokenInvalidLifetimeException ex)
        {
            _logger.LogWarning(ex, "Nyx relay callback JWT lifetime invalid");
            return new CallbackJwtValidationResult(false, ErrorCode: "callback_jwt_lifetime_invalid", ErrorSummary: ex.Message);
        }
        catch (SecurityTokenInvalidAlgorithmException ex)
        {
            _logger.LogWarning(ex, "Nyx relay callback JWT algorithm invalid");
            return new CallbackJwtValidationResult(false, ErrorCode: "callback_jwt_signature_invalid", ErrorSummary: ex.Message);
        }
        catch (SecurityTokenInvalidSignatureException ex)
        {
            _logger.LogWarning(ex, "Nyx relay callback JWT signature invalid");
            return new CallbackJwtValidationResult(false, ErrorCode: "callback_jwt_signature_invalid", ErrorSummary: ex.Message);
        }
        catch (SecurityTokenMalformedException ex)
        {
            _logger.LogWarning(ex, "Nyx relay callback JWT malformed");
            return new CallbackJwtValidationResult(false, ErrorCode: "callback_jwt_malformed", ErrorSummary: ex.Message);
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "Nyx relay callback JWT validation failed");
            return new CallbackJwtValidationResult(false, ErrorCode: "callback_jwt_signature_invalid", ErrorSummary: ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nyx relay callback JWT validation failed unexpectedly");
            return new CallbackJwtValidationResult(false, ErrorCode: "callback_jwt_invalid", ErrorSummary: ex.Message);
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

            return await LoadConfigurationAsync(ct);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<CachedOidcConfiguration> RefreshConfigurationAfterKidMissAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var cooldown = TimeSpan.FromSeconds(Math.Max(0, _relayOptions.JwksKidMissRefreshCooldownSeconds));
            if (cooldown > TimeSpan.Zero &&
                now - _lastForcedRefreshUtc < cooldown &&
                _cachedConfiguration is { } cached)
            {
                return cached;
            }

            _lastForcedRefreshUtc = now;
            _logger.LogInformation("Refreshing Nyx relay OIDC/JWKS cache after callback JWT signing key miss");
            return await LoadConfigurationAsync(ct);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<CachedOidcConfiguration> LoadConfigurationAsync(CancellationToken ct)
    {
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
            Audience: ResolveExpectedCallbackAudience(),
            JwksUri: jwksUri,
            SigningKeys: new JsonWebKeySet(jwksJson).GetSigningKeys().ToArray(),
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, _relayOptions.OidcCacheTtlSeconds)));

        _cachedConfiguration = configuration;
        return configuration;
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

    private string ResolveExpectedCallbackAudience()
    {
        if (!string.IsNullOrWhiteSpace(_relayOptions.CallbackExpectedAudience))
            return _relayOptions.CallbackExpectedAudience.Trim();

        return "channel-relay/callback";
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string ComputeBodySha256Hex(byte[] bodyBytes) =>
        Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();

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
