using System.Reflection;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatBrowserCredentialTests
{
    private static readonly MethodInfo ExtractAccessTokenMethod = typeof(NyxIdChatEndpoints)
        .GetMethod("ExtractNyxIdAccessToken", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ExtractNyxIdAccessToken not found.");

    [Fact]
    public void ExtractAccessToken_ShouldPreferDelegationAndNeverUseIdentityAssertion()
    {
        var delegated = new DefaultHttpContext();
        delegated.Request.Headers["X-NyxID-Delegation-Token"] = "delegation-token";
        Extract(delegated).Should().Be("delegation-token");

        var both = new DefaultHttpContext();
        both.Request.Headers.Authorization = "Bearer forwarded-access-token";
        both.Request.Headers["X-NyxID-Delegation-Token"] = "delegation-token";
        Extract(both).Should().Be("delegation-token");

        var identityOnly = new DefaultHttpContext();
        identityOnly.Request.Headers["X-NyxID-Identity-Token"] = "identity-assertion";
        Extract(identityOnly).Should().BeNull();

        var malformedDelegation = new DefaultHttpContext();
        malformedDelegation.Request.Headers.Authorization = "Bearer fallback-token";
        malformedDelegation.Request.Headers["X-NyxID-Delegation-Token"] = "token with spaces";
        Extract(malformedDelegation).Should().BeNull();
    }

    private static string? Extract(HttpContext http) =>
        (string?)ExtractAccessTokenMethod.Invoke(null, [http]);
}
