using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.Tests;

/// <summary>
/// Covers the relay scope fallback: when a bot has no aevatar mirror entry, the tenant scope is derived
/// from the bot owner's identity in the relay user token (scope_id ?? uid ?? sub).
/// </summary>
public class NyxIdRelayScopeFromUserTokenTests
{
    [Fact]
    public void ResolveScopeIdFromUserToken_ShouldPreferScopeIdClaim_OverUidAndSub()
    {
        var token = Jwt(
            new Claim("scope_id", "scope-explicit"),
            new Claim("uid", "uid-1"),
            new Claim(JwtRegisteredClaimNames.Sub, "sub-1"));

        NyxIdChatEndpoints.ResolveScopeIdFromUserToken(token).Should().Be("scope-explicit");
    }

    [Fact]
    public void ResolveScopeIdFromUserToken_ShouldFallBackToUid_WhenNoScopeId()
    {
        var token = Jwt(new Claim("uid", "uid-1"), new Claim(JwtRegisteredClaimNames.Sub, "sub-1"));

        NyxIdChatEndpoints.ResolveScopeIdFromUserToken(token).Should().Be("uid-1");
    }

    [Fact]
    public void ResolveScopeIdFromUserToken_ShouldFallBackToSub_WhenNoScopeIdOrUid()
    {
        // The production relay user token carries the bot owner's NyxID uuid as `sub`.
        var token = Jwt(new Claim(JwtRegisteredClaimNames.Sub, "owner-sub-1"), new Claim("scope", "read write"));

        NyxIdChatEndpoints.ResolveScopeIdFromUserToken(token).Should().Be("owner-sub-1");
    }

    [Fact]
    public void ResolveScopeIdFromUserToken_ShouldReturnNull_WhenTokenHasNoIdentityClaims()
    {
        var token = Jwt(new Claim("scope", "read write"));

        NyxIdChatEndpoints.ResolveScopeIdFromUserToken(token).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    [InlineData("user-token-1")]
    public void ResolveScopeIdFromUserToken_ShouldReturnNull_ForMissingOrUnparseableToken(string? token)
    {
        NyxIdChatEndpoints.ResolveScopeIdFromUserToken(token).Should().BeNull();
    }

    private static string Jwt(params Claim[] claims) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims: claims));
}
