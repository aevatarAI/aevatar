using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Authentication.Hosting;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.Bootstrap.Tests;

public sealed class DPoPProofValidatorTests
{
    private const string Method = "POST";
    private const string Uri = "https://api.example.com/resource";
    private const string AccessToken = "access-token-A";

    [Fact]
    public async Task ValidateAsync_WhenProofIsCorrectlyBound_ShouldSucceed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jkt = ComputeEcThumbprint(key);
        var proof = CreateProof(key, Method, Uri, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));

        var result = await CreateValidator().ValidateAsync(proof, AccessToken, jkt, Method, Uri, iatSkewSeconds: 60);

        result.Succeeded.Should().BeTrue(result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_WhenHtuDoesNotMatch_ShouldFail()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jkt = ComputeEcThumbprint(key);
        var proof = CreateProof(key, Method, "https://api.example.com/OTHER", DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));

        var result = await CreateValidator().ValidateAsync(proof, AccessToken, jkt, Method, Uri, iatSkewSeconds: 60);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("dpop_htu_mismatch");
    }

    [Fact]
    public async Task ValidateAsync_WhenHtmDoesNotMatch_ShouldFail()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jkt = ComputeEcThumbprint(key);
        var proof = CreateProof(key, "GET", Uri, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));

        var result = await CreateValidator().ValidateAsync(proof, AccessToken, jkt, Method, Uri, iatSkewSeconds: 60);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("dpop_htm_mismatch");
    }

    [Fact]
    public async Task ValidateAsync_WhenThumbprintDoesNotMatchCnf_ShouldFail()
    {
        using var proofKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var foreignThumbprint = ComputeEcThumbprint(otherKey);
        var proof = CreateProof(proofKey, Method, Uri, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));

        // The proof is signed by proofKey but the access token was bound to otherKey.
        var result = await CreateValidator().ValidateAsync(proof, AccessToken, foreignThumbprint, Method, Uri, iatSkewSeconds: 60);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("dpop_thumbprint_mismatch");
    }

    [Fact]
    public async Task ValidateAsync_WhenThumbprintLengthDoesNotMatch_ShouldFailWithoutThrowing()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var truncatedThumbprint = ComputeEcThumbprint(key)[..^1];
        var proof = CreateProof(key, Method, Uri, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));

        var result = await CreateValidator().ValidateAsync(
            proof,
            AccessToken,
            truncatedThumbprint,
            Method,
            Uri,
            iatSkewSeconds: 60);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("dpop_thumbprint_mismatch");
    }

    [Fact]
    public async Task ValidateAsync_WhenIatIsStale_ShouldFail()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jkt = ComputeEcThumbprint(key);
        var proof = CreateProof(key, Method, Uri, DateTimeOffset.UtcNow.AddMinutes(-10), Guid.NewGuid().ToString("N"));

        var result = await CreateValidator().ValidateAsync(proof, AccessToken, jkt, Method, Uri, iatSkewSeconds: 60);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("dpop_iat_stale");
    }

    [Fact]
    public async Task ValidateAsync_WhenIatIsOutsideDateTimeRange_ShouldFailWithoutThrowing()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jkt = ComputeEcThumbprint(key);
        var proof = CreateProof(
            key,
            Method,
            Uri,
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N"),
            iatSeconds: long.MaxValue);

        var result = await CreateValidator().ValidateAsync(
            proof,
            AccessToken,
            jkt,
            Method,
            Uri,
            iatSkewSeconds: 60);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("dpop_iat_missing");
    }

    [Fact]
    public async Task ValidateAsync_WhenJtiMissing_ShouldFail()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jkt = ComputeEcThumbprint(key);
        var proof = CreateProof(key, Method, Uri, DateTimeOffset.UtcNow, jti: null);

        var result = await CreateValidator().ValidateAsync(proof, AccessToken, jkt, Method, Uri, iatSkewSeconds: 60);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("dpop_jti_missing");
    }

    [Fact]
    public async Task ValidateAsync_WhenSignatureIsForged_ShouldFail()
    {
        using var proofKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jkt = ComputeEcThumbprint(proofKey);

        // Advertise proofKey's public jwk in the header, but sign with a different key.
        var proof = CreateProof(proofKey, Method, Uri, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), signWith: signingKey);

        var result = await CreateValidator().ValidateAsync(proof, AccessToken, jkt, Method, Uri, iatSkewSeconds: 60);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("dpop_signature_invalid");
    }

    [Fact]
    public async Task ValidateAsync_WhenReplayGuardRejects_ShouldFailAsReplay()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jkt = ComputeEcThumbprint(key);
        var proof = CreateProof(key, Method, Uri, DateTimeOffset.UtcNow, "repeated-jti");
        var validator = new DPoPProofValidator(new RejectingReplayGuard());

        var result = await validator.ValidateAsync(proof, AccessToken, jkt, Method, Uri, iatSkewSeconds: 60);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("dpop_jti_replayed");
    }

    [Fact]
    public async Task ValidateAsync_WhenAthTargetsAnotherAccessToken_ShouldFail()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jkt = ComputeEcThumbprint(key);
        var proof = CreateProof(
            key,
            Method,
            Uri,
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N"),
            athAccessToken: "access-token-B");

        var result = await CreateValidator().ValidateAsync(
            proof,
            AccessToken,
            jkt,
            Method,
            Uri,
            iatSkewSeconds: 60);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("dpop_ath_mismatch");
    }

    [Fact]
    public async Task ValidateAsync_WhenAthIsMissing_ShouldFail()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jkt = ComputeEcThumbprint(key);
        var proof = CreateProof(
            key,
            Method,
            Uri,
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N"),
            athAccessToken: null);

        var result = await CreateValidator().ValidateAsync(
            proof,
            AccessToken,
            jkt,
            Method,
            Uri,
            iatSkewSeconds: 60);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("dpop_ath_missing");
    }

    [Fact]
    public async Task ValidateAsync_WhenProofMissing_ShouldFail()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jkt = ComputeEcThumbprint(key);

        var result = await CreateValidator().ValidateAsync(null, AccessToken, jkt, Method, Uri, iatSkewSeconds: 60);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("dpop_proof_missing");
    }

    [Fact]
    public async Task NoOpDPoPReplayGuard_ShouldAlwaysTreatJtiAsFresh()
    {
        var guard = new NoOpDPoPReplayGuard();

        (await guard.TryRegisterAsync("any-jti")).Should().BeTrue();
    }

    [Fact]
    public async Task OnTokenValidated_WhenDPoPDisabled_ShouldNotFailEvenWithCnfTokenAndNoProof()
    {
        var context = CreateTokenValidatedContext(
            dpopEnabled: false,
            cnfThumbprint: "some-jkt",
            proofHeader: null);

        await InvokeOnTokenValidated(context);

        context.Result?.Failure.Should().BeNull();
    }

    [Fact]
    public async Task OnTokenValidated_WhenDPoPEnabledAndCnfTokenHasNoProof_ShouldFail()
    {
        var context = CreateTokenValidatedContext(
            dpopEnabled: true,
            cnfThumbprint: "some-jkt",
            proofHeader: null);

        await InvokeOnTokenValidated(context);

        context.Result.Should().NotBeNull();
        context.Result!.Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task OnTokenValidated_WhenDPoPEnabledButTokenHasNoCnf_ShouldNotFail()
    {
        var context = CreateTokenValidatedContext(
            dpopEnabled: true,
            cnfThumbprint: null,
            proofHeader: null);

        await InvokeOnTokenValidated(context);

        // Plain bearer token (no sender constraint): nothing to enforce.
        context.Result?.Failure.Should().BeNull();
    }

    private static async Task InvokeOnTokenValidated(
        Microsoft.AspNetCore.Authentication.JwtBearer.TokenValidatedContext context)
    {
        var method = typeof(Aevatar.Authentication.Hosting.AevatarAuthenticationHostExtensions).GetMethod(
            "OnTokenValidatedValidateDPoP",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("OnTokenValidatedValidateDPoP not found.");

        await (Task)method.Invoke(null, [context])!;
    }

    private static Microsoft.AspNetCore.Authentication.JwtBearer.TokenValidatedContext
        CreateTokenValidatedContext(bool dpopEnabled, string? cnfThumbprint, string? proofHeader)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Options.IOptions<Aevatar.Authentication.Abstractions.AevatarAuthenticationOptions>>(
            Microsoft.Extensions.Options.Options.Create(new Aevatar.Authentication.Abstractions.AevatarAuthenticationOptions
            {
                DPoP = new Aevatar.Authentication.Abstractions.DPoPOptions { Enabled = dpopEnabled },
            }));
        services.AddSingleton<DPoPProofValidator>();
        var provider = services.BuildServiceProvider();

        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = provider,
        };
        httpContext.Request.Method = Method;
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new Microsoft.AspNetCore.Http.HostString("api.example.com");
        httpContext.Request.Path = "/resource";
        if (proofHeader is not null)
            httpContext.Request.Headers["DPoP"] = proofHeader;

        var claims = new List<System.Security.Claims.Claim>();
        if (cnfThumbprint is not null)
            claims.Add(new System.Security.Claims.Claim("cnf", $"{{\"jkt\":\"{cnfThumbprint}\"}}"));
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(claims, "Bearer"));

        var scheme = new Microsoft.AspNetCore.Authentication.AuthenticationScheme(
            "Bearer",
            "Bearer",
            typeof(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler));
        var jwtOptions = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions();

        return new Microsoft.AspNetCore.Authentication.JwtBearer.TokenValidatedContext(httpContext, scheme, jwtOptions)
        {
            Principal = principal,
        };
    }

    private static DPoPProofValidator CreateValidator() => new();

    /// <summary>
    /// Builds a DPoP proof JWT: header carries typ=dpop+jwt and the public jwk, payload carries
    /// htm/htu/iat/jti/ath, and the whole thing is ES256-signed with <paramref name="signWith"/>
    /// (defaults to <paramref name="key"/>).
    /// </summary>
    private static string CreateProof(
        ECDsa key,
        string htm,
        string htu,
        DateTimeOffset iat,
        string? jti,
        ECDsa? signWith = null,
        string? athAccessToken = AccessToken,
        long? iatSeconds = null)
    {
        var publicJwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(key));
        var jwkJson =
            $"{{\"kty\":\"EC\",\"crv\":\"{publicJwk.Crv}\",\"x\":\"{publicJwk.X}\",\"y\":\"{publicJwk.Y}\"}}";

        var header = $"{{\"typ\":\"dpop+jwt\",\"alg\":\"ES256\",\"jwk\":{jwkJson}}}";

        var payloadMembers = new List<string>
        {
            $"\"htm\":\"{htm}\"",
            $"\"htu\":\"{htu}\"",
            $"\"iat\":{iatSeconds ?? iat.ToUnixTimeSeconds()}",
        };
        if (jti is not null)
            payloadMembers.Add($"\"jti\":\"{jti}\"");
        if (athAccessToken is not null)
        {
            var ath = Base64UrlEncoder.Encode(
                SHA256.HashData(Encoding.UTF8.GetBytes(athAccessToken)));
            payloadMembers.Add($"\"ath\":\"{ath}\"");
        }
        var payload = "{" + string.Join(",", payloadMembers) + "}";

        var signingInput =
            $"{Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(header))}." +
            $"{Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(payload))}";

        var signature = (signWith ?? key).SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256);

        return $"{signingInput}.{Base64UrlEncoder.Encode(signature)}";
    }

    /// <summary>Independent RFC 7638 EC thumbprint, to avoid testing the SUT against itself.</summary>
    private static string ComputeEcThumbprint(ECDsa key)
    {
        var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(key));
        var canonical = $"{{\"crv\":\"{jwk.Crv}\",\"kty\":\"EC\",\"x\":\"{jwk.X}\",\"y\":\"{jwk.Y}\"}}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Base64UrlEncoder.Encode(digest);
    }

    private sealed class RejectingReplayGuard : IDPoPReplayGuard
    {
        public ValueTask<bool> TryRegisterAsync(string jti, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }
}
