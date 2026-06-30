using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.Mainnet.Host.Api.Responses;

internal sealed record NyxIdIdentityAssertionValidationResult(
    bool Succeeded,
    string? Subject = null,
    string? ErrorCode = null,
    string? ErrorSummary = null);

internal sealed record NyxIdJwksCacheEntry(
    string Issuer,
    string? Audience,
    IReadOnlyList<SecurityKey> SigningKeys,
    DateTimeOffset ExpiresAtUtc);

/// <summary>refactor helper, no behavior change</summary>
internal sealed class NyxIdIdentityAssertionValidator
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ResponsesNyxIdIdentityAssertionOptions _options;
    private readonly NyxIdToolOptions? _nyxIdOptions;
    private readonly ILogger<NyxIdIdentityAssertionValidator> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private NyxIdJwksCacheEntry? _cachedKeys;
    private DateTimeOffset _lastForcedRefreshUtc = DateTimeOffset.MinValue;

    // NyxIdToolOptions is registered as a plain singleton by AddNyxIdTools (carrying the real
    // NyxID authority), NOT via Configure<NyxIdToolOptions>. Depend on it directly: an
    // IOptions<NyxIdToolOptions> here would silently resolve to an empty default instance
    // (BaseUrl == null), which left the issuer/authority fallback dead and surfaced as the
    // generic "issuer is not configured" identity_assertion_invalid failure in production.
    public NyxIdIdentityAssertionValidator(
        IHttpClientFactory httpClientFactory,
        IOptions<ResponsesNyxIdIdentityAssertionOptions> options,
        NyxIdToolOptions? nyxIdOptions = null,
        ILogger<NyxIdIdentityAssertionValidator>? logger = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _nyxIdOptions = nyxIdOptions;
        _logger = logger ?? NullLogger<NyxIdIdentityAssertionValidator>.Instance;
    }

    public async Task<NyxIdIdentityAssertionValidationResult> ValidateAsync(
        string identityToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(identityToken))
            return Fail("identity_assertion_missing", "NyxID identity assertion is missing.");

        try
        {
            var keys = await GetKeysAsync(forceRefresh: false, ct);
            var initial = ValidateCore(identityToken.Trim(), keys);
            if (initial.Succeeded)
                return initial;

            if (!string.Equals(initial.ErrorCode, "identity_assertion_kid_not_found", StringComparison.Ordinal))
                return initial;

            var refreshed = await RefreshKeysAfterKidMissAsync(ct);
            return ValidateCore(identityToken.Trim(), refreshed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID identity assertion validation failed unexpectedly");
            return Fail("identity_assertion_invalid", ex.Message);
        }
    }

    private NyxIdIdentityAssertionValidationResult ValidateCore(
        string identityToken,
        NyxIdJwksCacheEntry keys)
    {
        var handler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false,
        };
        var parameters = new TokenValidationParameters
        {
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateIssuer = true,
            ValidIssuer = keys.Issuer,
            ValidateAudience = keys.Audience is not null,
            ValidAudience = keys.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(Math.Max(0, _options.ClockSkewSeconds)),
        };

        try
        {
            var principal = handler.ValidateToken(identityToken, parameters, out _);
            var subject = NormalizeOptional(principal.FindFirstValue(JwtRegisteredClaimNames.Sub)) ??
                          NormalizeOptional(principal.FindFirstValue("sub"));
            var jti = NormalizeOptional(principal.FindFirstValue(JwtRegisteredClaimNames.Jti)) ??
                      NormalizeOptional(principal.FindFirstValue("jti"));

            if (subject is null)
                return Fail("identity_assertion_sub_missing", "NyxID identity assertion is missing sub.");

            if (jti is null)
                return Fail("identity_assertion_jti_missing", "NyxID identity assertion is missing jti.");

            var expectedServiceId = NormalizeOptional(_options.ExpectedServiceId);
            if (expectedServiceId is not null)
            {
                var actualServiceId = NormalizeOptional(principal.FindFirstValue("nyx_service_id"));
                if (!string.Equals(actualServiceId, expectedServiceId, StringComparison.Ordinal))
                {
                    return Fail(
                        "identity_assertion_service_id_mismatch",
                        "NyxID identity assertion service id does not match the expected service.");
                }
            }

            return new NyxIdIdentityAssertionValidationResult(true, Subject: subject);
        }
        catch (SecurityTokenSignatureKeyNotFoundException ex)
        {
            _logger.LogWarning(ex, "NyxID identity assertion signing key not found");
            return Fail("identity_assertion_kid_not_found", ex.Message);
        }
        catch (SecurityTokenInvalidIssuerException ex)
        {
            _logger.LogWarning(ex, "NyxID identity assertion issuer mismatch");
            return Fail("identity_assertion_issuer_mismatch", ex.Message);
        }
        catch (SecurityTokenInvalidAudienceException ex)
        {
            _logger.LogWarning(ex, "NyxID identity assertion audience mismatch");
            return Fail("identity_assertion_audience_mismatch", ex.Message);
        }
        catch (SecurityTokenExpiredException ex)
        {
            _logger.LogWarning(ex, "NyxID identity assertion expired");
            return Fail("identity_assertion_lifetime_invalid", ex.Message);
        }
        catch (SecurityTokenNotYetValidException ex)
        {
            _logger.LogWarning(ex, "NyxID identity assertion not yet valid");
            return Fail("identity_assertion_lifetime_invalid", ex.Message);
        }
        catch (SecurityTokenInvalidLifetimeException ex)
        {
            _logger.LogWarning(ex, "NyxID identity assertion lifetime invalid");
            return Fail("identity_assertion_lifetime_invalid", ex.Message);
        }
        catch (SecurityTokenInvalidAlgorithmException ex)
        {
            _logger.LogWarning(ex, "NyxID identity assertion algorithm invalid");
            return Fail("identity_assertion_signature_invalid", ex.Message);
        }
        catch (SecurityTokenInvalidSignatureException ex)
        {
            _logger.LogWarning(ex, "NyxID identity assertion signature invalid");
            return Fail("identity_assertion_signature_invalid", ex.Message);
        }
        catch (SecurityTokenMalformedException ex)
        {
            _logger.LogWarning(ex, "NyxID identity assertion malformed");
            return Fail("identity_assertion_malformed", ex.Message);
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "NyxID identity assertion validation failed");
            return Fail("identity_assertion_invalid", ex.Message);
        }
    }

    private async Task<NyxIdJwksCacheEntry> GetKeysAsync(bool forceRefresh, CancellationToken ct)
    {
        var cached = _cachedKeys;
        if (!forceRefresh && cached is not null && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
            return cached;

        await _refreshLock.WaitAsync(ct);
        try
        {
            cached = _cachedKeys;
            if (!forceRefresh && cached is not null && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
                return cached;

            return await LoadKeysAsync(ct);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<NyxIdJwksCacheEntry> RefreshKeysAfterKidMissAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var cooldown = TimeSpan.FromSeconds(Math.Max(0, _options.KidMissRefreshCooldownSeconds));
            if (cooldown > TimeSpan.Zero &&
                now - _lastForcedRefreshUtc < cooldown &&
                _cachedKeys is { } cached)
            {
                return cached;
            }

            _lastForcedRefreshUtc = now;
            return await LoadKeysAsync(ct);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<NyxIdJwksCacheEntry> LoadKeysAsync(CancellationToken ct)
    {
        var issuer = ResolveIssuer();
        var audience = ResolveAudience();
        var jwksUri = await ResolveJwksUriAsync(ct);

        var http = _httpClientFactory.CreateClient();
        using var jwksResponse = await http.GetAsync(jwksUri, ct);
        jwksResponse.EnsureSuccessStatusCode();
        var jwksJson = await jwksResponse.Content.ReadAsStringAsync(ct);

        var keys = new NyxIdJwksCacheEntry(
            issuer,
            audience,
            new JsonWebKeySet(jwksJson).GetSigningKeys().ToArray(),
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, _options.JwksCacheTtlSeconds)));

        _cachedKeys = keys;
        return keys;
    }

    private async Task<string> ResolveJwksUriAsync(CancellationToken ct)
    {
        var configuredJwksUri = NormalizeOptional(_options.JwksUri);
        if (configuredJwksUri is not null)
            return configuredJwksUri;

        var discoveryUrl = ResolveDiscoveryUrl();
        var http = _httpClientFactory.CreateClient();
        using var discoveryResponse = await http.GetAsync(discoveryUrl, ct);
        discoveryResponse.EnsureSuccessStatusCode();
        var discoveryJson = await discoveryResponse.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(discoveryJson);

        return RequireString(document.RootElement, "jwks_uri");
    }

    private string ResolveIssuer()
    {
        var issuer = NormalizeOptional(_options.Issuer);
        if (issuer is not null)
            return issuer;

        var authority = ResolveNyxIdAuthority();
        if (authority is not null)
            return authority;

        throw new InvalidOperationException("NyxID identity assertion issuer is not configured.");
    }

    // Audience is optional. The proxy mints an assertion for THIS service only when forwarding
    // to it, and the issuer + RSA signature already establish NyxID as the trusted minter. When
    // ExpectedAudience is configured it is enforced (defense-in-depth against cross-service token
    // replay); when absent, audience validation is skipped rather than failing closed.
    private string? ResolveAudience() => NormalizeOptional(_options.ExpectedAudience);

    private string ResolveDiscoveryUrl()
    {
        var discoveryUrl = NormalizeOptional(_options.OidcDiscoveryUrl);
        if (discoveryUrl is not null)
            return discoveryUrl;

        var authority = ResolveNyxIdAuthority();
        if (authority is null)
            throw new InvalidOperationException("NyxID identity assertion discovery URL is not configured.");

        return $"{authority}/.well-known/openid-configuration";
    }

    private string? ResolveNyxIdAuthority() =>
        NormalizeOptional(_nyxIdOptions?.BaseUrl)?.TrimEnd('/')
        ?? NormalizeOptional(_options.Issuer)?.TrimEnd('/');

    private static NyxIdIdentityAssertionValidationResult Fail(string code, string? summary) =>
        new(false, ErrorCode: code, ErrorSummary: summary);

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
                $"NyxID identity assertion discovery document is missing required string property '{propertyName}'.");
        }

        return property.GetString() ?? throw new InvalidOperationException(
            $"NyxID identity assertion discovery property '{propertyName}' is null.");
    }
}
