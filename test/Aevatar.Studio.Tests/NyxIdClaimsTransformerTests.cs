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
    public void TransformClaims_ShouldYieldNothing_WhenOnlyGenericIdClaimPresent()
    {
        // Hardening (M7): the generic "any *_id" fallback was removed. A token whose scope
        // only lives in an arbitrary *_id claim (e.g. org_id / team_id / tenant_id) now maps
        // to NO scope_id, so the caller is denied rather than bound to an unvetted claim.
        var principal = CreatePrincipal(
            new Claim("org_id", "o-1"),
            new Claim("team_id", "t-1"),
            new Claim("tenant_id", "ten-1"));
        _transformer.TransformClaims(principal).Should().BeEmpty();
    }

    [Fact]
    public void TransformClaims_ShouldYieldNothing_WhenClientIdAndSessionIdPresent()
    {
        var principal = CreatePrincipal(
            new Claim("client_id", "c1"),
            new Claim("session_id", "s1"),
            new Claim("sid", "sid1"));
        _transformer.TransformClaims(principal).Should().BeEmpty();
    }

    [Fact]
    public void TransformClaims_ShouldMapExplicitSub_WhenGenericIdClaimAlsoPresent()
    {
        // An explicit known claim still maps; the sibling generic *_id claim is never consulted.
        var principal = CreatePrincipal(
            new Claim("sub", "sub-val"),
            new Claim("tenant_id", "tenant-val"));
        var claims = _transformer.TransformClaims(principal).ToList();
        claims.Should().ContainSingle();
        claims[0].Type.Should().Be(AevatarStandardClaimTypes.ScopeId);
        claims[0].Value.Should().Be("sub-val");
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
