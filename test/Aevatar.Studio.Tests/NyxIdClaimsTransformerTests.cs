using System.Security.Claims;
using Aevatar.Authentication.Abstractions;
using Aevatar.Authentication.Providers.NyxId;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class NyxIdClaimsTransformerTests
{
    private readonly NyxIdClaimsTransformer _transformer = new();

    private static ClaimsPrincipal CreatePrincipal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public void TransformClaims_ShouldYieldNothing_WhenIdentityIsNull()
    {
        var principal = new ClaimsPrincipal();
        _transformer.TransformClaims(principal).Should().BeEmpty();
    }

    [Fact]
    public void TransformClaims_ShouldYieldNothing_WhenScopeIdAlreadyPresent()
    {
        var principal = CreatePrincipal(
            new Claim(AevatarStandardClaimTypes.ScopeId, "existing-scope"));
        _transformer.TransformClaims(principal).Should().BeEmpty();
    }

    [Fact]
    public void TransformClaims_ShouldMapUid_WhenNoScopeId()
    {
        var principal = CreatePrincipal(new Claim("uid", "user-123"));
        var claims = _transformer.TransformClaims(principal).ToList();
        claims.Should().ContainSingle();
        claims[0].Type.Should().Be(AevatarStandardClaimTypes.ScopeId);
        claims[0].Value.Should().Be("user-123");
    }

    [Fact]
    public void TransformClaims_ShouldMapSub_WhenNoUid()
    {
        var principal = CreatePrincipal(new Claim("sub", "sub-456"));
        var claims = _transformer.TransformClaims(principal).ToList();
        claims.Should().ContainSingle();
        claims[0].Value.Should().Be("sub-456");
    }

    [Fact]
    public void TransformClaims_ShouldMapNameIdentifier_WhenNoSubOrUid()
    {
        var principal = CreatePrincipal(
            new Claim(ClaimTypes.NameIdentifier, "name-789"));
        var claims = _transformer.TransformClaims(principal).ToList();
        claims.Should().ContainSingle();
        claims[0].Value.Should().Be("name-789");
    }

    [Fact]
    public void TransformClaims_ShouldFallbackToGenericIdClaim()
    {
        var principal = CreatePrincipal(new Claim("tenant_id", "t-1"));
        var claims = _transformer.TransformClaims(principal).ToList();
        claims.Should().ContainSingle();
        claims[0].Value.Should().Be("t-1");
    }

    [Fact]
    public void TransformClaims_ShouldIgnoreClientIdAndSessionId()
    {
        var principal = CreatePrincipal(
            new Claim("client_id", "c1"),
            new Claim("session_id", "s1"),
            new Claim("sid", "sid1"));
        _transformer.TransformClaims(principal).Should().BeEmpty();
    }

    [Fact]
    public void TransformClaims_ShouldPreferUidOverGenericId()
    {
        var principal = CreatePrincipal(
            new Claim("uid", "uid-val"),
            new Claim("tenant_id", "tenant-val"));
        var claims = _transformer.TransformClaims(principal).ToList();
        claims.Should().ContainSingle();
        claims[0].Value.Should().Be("uid-val");
    }

    [Fact]
    public void TransformClaims_ShouldTrimValues()
    {
        var principal = CreatePrincipal(new Claim("uid", "  trimmed  "));
        var claims = _transformer.TransformClaims(principal).ToList();
        claims[0].Value.Should().Be("trimmed");
    }

    [Fact]
    public void TransformClaims_ShouldSkipBlankValues()
    {
        var principal = CreatePrincipal(
            new Claim("uid", "  "),
            new Claim("sub", "valid-sub"));
        var claims = _transformer.TransformClaims(principal).ToList();
        claims.Should().ContainSingle();
        claims[0].Value.Should().Be("valid-sub");
    }
}
