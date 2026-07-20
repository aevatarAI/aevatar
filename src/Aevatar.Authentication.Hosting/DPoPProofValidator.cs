using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.Authentication.Hosting;

/// <summary>Outcome of a single DPoP proof validation.</summary>
public sealed record DPoPValidationResult(bool Succeeded, string? ErrorCode = null, string? ErrorSummary = null)
{
    public static DPoPValidationResult Success { get; } = new(true);

    public static DPoPValidationResult Fail(string code, string summary) => new(false, code, summary);
}

/// <summary>
/// Validates an RFC 9449 DPoP proof JWT against the confirmed key of an access token.
/// <para>
/// Modeled on <c>NyxIdIdentityAssertionValidator</c>'s validation style, but the proof is
/// self-contained: its verification key is the <c>jwk</c> carried in the proof header (no
/// remote JWKS). The binding to the access token is established by comparing the proof key's
/// RFC 7638 thumbprint against the access token's <c>cnf.jkt</c> confirmation claim.
/// </para>
/// <para>
/// Checks performed:
/// <list type="number">
/// <item>the proof's own signature verifies against the <c>jwk</c> in its header;</item>
/// <item><c>typ</c> is <c>dpop+jwt</c> and an asymmetric <c>alg</c> is used;</item>
/// <item><c>htm</c> equals the request method and <c>htu</c> equals the request URI;</item>
/// <item><c>iat</c> is within the configured freshness window;</item>
/// <item>the proof <c>jwk</c> thumbprint (RFC 7638) equals the access token <c>cnf.jkt</c>;</item>
/// <item><c>ath</c> equals the SHA-256 hash of the presented access token;</item>
/// <item><c>jti</c> is present and (via <see cref="IDPoPReplayGuard"/>) not a replay.</item>
/// </list>
/// </para>
/// </summary>
public sealed class DPoPProofValidator
{
    private const string DPoPTokenType = "dpop+jwt";

    private static readonly HashSet<string> AllowedProofAlgorithms = new(StringComparer.Ordinal)
    {
        SecurityAlgorithms.RsaSha256,
        SecurityAlgorithms.RsaSha384,
        SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256,
        SecurityAlgorithms.RsaSsaPssSha384,
        SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256,
        SecurityAlgorithms.EcdsaSha384,
        SecurityAlgorithms.EcdsaSha512,
    };

    private readonly IDPoPReplayGuard _replayGuard;
    private readonly ILogger<DPoPProofValidator> _logger;

    public DPoPProofValidator(
        IDPoPReplayGuard? replayGuard = null,
        ILogger<DPoPProofValidator>? logger = null)
    {
        _replayGuard = replayGuard ?? new NoOpDPoPReplayGuard();
        _logger = logger ?? NullLogger<DPoPProofValidator>.Instance;
    }

    /// <summary>
    /// Validates <paramref name="proofToken"/> for a request bound to
    /// <paramref name="confirmationThumbprint"/> (the access token's <c>cnf.jkt</c>).
    /// </summary>
    /// <param name="proofToken">The value of the request's <c>DPoP</c> header.</param>
    /// <param name="confirmationThumbprint">The access token <c>cnf.jkt</c> thumbprint.</param>
    /// <param name="httpMethod">The actual request method (e.g. <c>POST</c>).</param>
    /// <param name="httpUri">The actual request absolute URI.</param>
    /// <param name="iatSkewSeconds">Freshness window for the proof <c>iat</c>, in seconds.</param>
    public async Task<DPoPValidationResult> ValidateAsync(
        string? proofToken,
        string? accessToken,
        string? confirmationThumbprint,
        string httpMethod,
        string httpUri,
        int iatSkewSeconds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(proofToken))
            return DPoPValidationResult.Fail("dpop_proof_missing", "DPoP proof header is missing.");

        if (string.IsNullOrWhiteSpace(accessToken))
            return DPoPValidationResult.Fail("dpop_access_token_missing", "Presented access token is missing.");

        if (string.IsNullOrWhiteSpace(confirmationThumbprint))
            return DPoPValidationResult.Fail("dpop_cnf_missing", "Access token has no cnf.jkt confirmation.");

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        if (!handler.CanReadToken(proofToken))
            return DPoPValidationResult.Fail("dpop_proof_malformed", "DPoP proof is not a readable JWT.");

        JwtSecurityToken proof;
        try
        {
            proof = handler.ReadJwtToken(proofToken);
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException)
        {
            _logger.LogWarning(ex, "DPoP proof could not be parsed");
            return DPoPValidationResult.Fail("dpop_proof_malformed", ex.Message);
        }

        if (!string.Equals(proof.Header.Typ, DPoPTokenType, StringComparison.OrdinalIgnoreCase))
            return DPoPValidationResult.Fail("dpop_typ_invalid", "DPoP proof typ must be dpop+jwt.");

        if (!AllowedProofAlgorithms.Contains(proof.Header.Alg))
            return DPoPValidationResult.Fail("dpop_alg_invalid", $"DPoP proof alg '{proof.Header.Alg}' is not allowed.");

        var jwk = ReadHeaderJwk(proofToken);
        if (jwk is null)
            return DPoPValidationResult.Fail("dpop_jwk_missing", "DPoP proof header is missing a public jwk.");

        // (a) verify the proof's own signature against the embedded public key.
        var signatureResult = VerifyProofSignature(handler, proofToken, jwk, proof.Header.Alg);
        if (!signatureResult.Succeeded)
            return signatureResult;

        // (d) the embedded key's thumbprint must equal the access token's cnf.jkt.
        string proofThumbprint;
        try
        {
            proofThumbprint = ComputeRfc7638Thumbprint(jwk);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            _logger.LogWarning(ex, "DPoP proof jwk thumbprint could not be computed");
            return DPoPValidationResult.Fail("dpop_jwk_invalid", ex.Message);
        }

        if (!FixedTimeEquals(proofThumbprint, confirmationThumbprint.Trim()))
            return DPoPValidationResult.Fail("dpop_thumbprint_mismatch", "DPoP proof key does not match cnf.jkt.");

        // (b) htm / htu must match the actual request.
        var htm = proof.Payload.TryGetValue("htm", out var htmValue) ? htmValue?.ToString() : null;
        if (!string.Equals(htm, httpMethod, StringComparison.OrdinalIgnoreCase))
            return DPoPValidationResult.Fail("dpop_htm_mismatch", "DPoP proof htm does not match the request method.");

        var htu = proof.Payload.TryGetValue("htu", out var htuValue) ? htuValue?.ToString() : null;
        if (!HtuMatches(htu, httpUri))
            return DPoPValidationResult.Fail("dpop_htu_mismatch", "DPoP proof htu does not match the request URI.");

        // RFC 9449 section 7: a protected-resource proof is bound to the exact serialized
        // access token, not merely to the same proof key.
        var accessTokenBinding = ValidateAccessTokenBinding(proof, accessToken);
        if (!accessTokenBinding.Succeeded)
            return accessTokenBinding;

        // (c) iat freshness window.
        if (!TryGetIat(proof, out var iat))
            return DPoPValidationResult.Fail("dpop_iat_missing", "DPoP proof is missing iat.");

        var skew = TimeSpan.FromSeconds(Math.Max(0, iatSkewSeconds));
        var age = DateTimeOffset.UtcNow - iat;
        if (age > skew || age < -skew)
            return DPoPValidationResult.Fail("dpop_iat_stale", "DPoP proof iat is outside the freshness window.");

        // (e) jti must be present; replay is delegated to the injectable guard.
        var jti = proof.Payload.TryGetValue("jti", out var jtiValue) ? jtiValue?.ToString() : null;
        if (string.IsNullOrWhiteSpace(jti))
            return DPoPValidationResult.Fail("dpop_jti_missing", "DPoP proof is missing jti.");

        var fresh = await _replayGuard.TryRegisterAsync(jti, cancellationToken);
        if (!fresh)
            return DPoPValidationResult.Fail("dpop_jti_replayed", "DPoP proof jti has already been used.");

        return DPoPValidationResult.Success;
    }

    private DPoPValidationResult VerifyProofSignature(
        JwtSecurityTokenHandler handler,
        string proofToken,
        JsonWebKey jwk,
        string algorithm)
    {
        try
        {
            var parameters = new TokenValidationParameters
            {
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = false,
                IssuerSigningKey = jwk,
                ValidAlgorithms = [algorithm],
                ValidateIssuer = false,
                ValidateAudience = false,
                // htm/htu/iat carry the request binding and freshness; the base lifetime
                // machinery is bypassed here because a DPoP proof has no exp.
                ValidateLifetime = false,
                RequireExpirationTime = false,
            };

            handler.ValidateToken(proofToken, parameters, out _);
            return DPoPValidationResult.Success;
        }
        catch (SecurityTokenInvalidSignatureException ex)
        {
            _logger.LogWarning(ex, "DPoP proof signature invalid");
            return DPoPValidationResult.Fail("dpop_signature_invalid", ex.Message);
        }
        catch (SecurityTokenInvalidAlgorithmException ex)
        {
            _logger.LogWarning(ex, "DPoP proof algorithm invalid");
            return DPoPValidationResult.Fail("dpop_signature_invalid", ex.Message);
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "DPoP proof signature validation failed");
            return DPoPValidationResult.Fail("dpop_signature_invalid", ex.Message);
        }
    }

    private static JsonWebKey? ReadHeaderJwk(string proofToken)
    {
        // Parse the raw base64url header segment directly rather than reading proof.Header["jwk"],
        // whose runtime type (JsonElement vs dictionary) varies by library version. The header
        // segment is the substring before the first '.'.
        var firstDot = proofToken.IndexOf('.');
        if (firstDot <= 0)
            return null;

        string headerJson;
        try
        {
            headerJson = Base64UrlEncoder.Decode(proofToken[..firstDot]);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(headerJson);
            if (!document.RootElement.TryGetProperty("jwk", out var jwkElement) ||
                jwkElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var jwk = JsonWebKey.Create(jwkElement.GetRawText());
            // A DPoP proof header MUST carry only the PUBLIC key; a private member here is
            // an attempt to smuggle a signing key and is rejected.
            return HasPrivateMaterial(jwk) ? null : jwk;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool HasPrivateMaterial(JsonWebKey jwk) =>
        !string.IsNullOrEmpty(jwk.D) ||
        !string.IsNullOrEmpty(jwk.DP) ||
        !string.IsNullOrEmpty(jwk.DQ) ||
        !string.IsNullOrEmpty(jwk.P) ||
        !string.IsNullOrEmpty(jwk.Q) ||
        !string.IsNullOrEmpty(jwk.QI) ||
        !string.IsNullOrEmpty(jwk.K);

    /// <summary>
    /// Computes the RFC 7638 JWK thumbprint (SHA-256, base64url) from the canonical required
    /// members of the key type. Computed explicitly rather than via a library helper so the
    /// exact member ordering is auditable and version-independent.
    /// </summary>
    internal static string ComputeRfc7638Thumbprint(JsonWebKey jwk)
    {
        ArgumentNullException.ThrowIfNull(jwk);

        var canonical = jwk.Kty switch
        {
            JsonWebAlgorithmsKeyTypes.RSA =>
                $"{{\"e\":\"{Require(jwk.E, "e")}\",\"kty\":\"RSA\",\"n\":\"{Require(jwk.N, "n")}\"}}",
            JsonWebAlgorithmsKeyTypes.EllipticCurve =>
                $"{{\"crv\":\"{Require(jwk.Crv, "crv")}\",\"kty\":\"EC\",\"x\":\"{Require(jwk.X, "x")}\",\"y\":\"{Require(jwk.Y, "y")}\"}}",
            JsonWebAlgorithmsKeyTypes.Octet =>
                $"{{\"k\":\"{Require(jwk.K, "k")}\",\"kty\":\"oct\"}}",
            _ => throw new InvalidOperationException($"Unsupported DPoP jwk kty '{jwk.Kty}'."),
        };

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Base64UrlEncoder.Encode(digest);
    }

    private static bool HtuMatches(string? htu, string httpUri)
    {
        if (string.IsNullOrWhiteSpace(htu) || string.IsNullOrWhiteSpace(httpUri))
            return false;

        // RFC 9449: htu is compared without query/fragment. Normalize both sides.
        return string.Equals(NormalizeHtu(htu), NormalizeHtu(httpUri), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHtu(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');

        var withoutFragment = value.Split('#', 2)[0];
        var withoutQuery = withoutFragment.Split('?', 2)[0];
        return withoutQuery.TrimEnd('/');
    }

    private static bool TryGetIat(JwtSecurityToken proof, out DateTimeOffset iat)
    {
        iat = default;
        if (!proof.Payload.TryGetValue("iat", out var iatValue) || iatValue is null)
            return false;

        long seconds;
        switch (iatValue)
        {
            case long l:
                seconds = l;
                break;
            case int i:
                seconds = i;
                break;
            case JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var parsed):
                seconds = parsed;
                break;
            default:
                if (!long.TryParse(iatValue.ToString(), out seconds))
                    return false;
                break;
        }

        try
        {
            iat = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static DPoPValidationResult ValidateAccessTokenBinding(
        JwtSecurityToken proof,
        string accessToken)
    {
        var ath = proof.Payload.TryGetValue("ath", out var athValue) ? athValue?.ToString() : null;
        if (string.IsNullOrWhiteSpace(ath))
            return DPoPValidationResult.Fail("dpop_ath_missing", "DPoP proof is missing ath.");

        var expectedAth = Base64UrlEncoder.Encode(
            SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));
        return FixedTimeEquals(ath, expectedAth)
            ? DPoPValidationResult.Success
            : DPoPValidationResult.Fail(
                "dpop_ath_mismatch",
                "DPoP proof ath does not match the access token.");
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        if (leftBytes.Length != rightBytes.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Require(string? value, string member) =>
        string.IsNullOrEmpty(value)
            ? throw new InvalidOperationException($"DPoP jwk is missing required member '{member}'.")
            : value;
}
