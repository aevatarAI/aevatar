using System.Reflection;
using System.Text;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatBrowserCredentialTests
{
    private static readonly MethodInfo ExtractAccessTokenMethod = typeof(NyxIdChatEndpoints)
        .GetMethod("ExtractNyxIdAccessToken", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ExtractNyxIdAccessToken not found.");
    private static readonly MethodInfo ExtractCredentialsMethod = typeof(NyxIdChatEndpoints)
        .GetMethod("ExtractNyxIdCredentials", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ExtractNyxIdCredentials not found.");

    [Fact]
    public void ExtractAccessToken_ShouldPreferForwardedBearerAndNeverUseIdentityAssertion()
    {
        var delegated = new DefaultHttpContext();
        delegated.Request.Headers["X-NyxID-Delegation-Token"] = "delegation-token";
        Extract(delegated).Should().Be("delegation-token");

        var both = new DefaultHttpContext();
        both.Request.Headers.Authorization = "Bearer forwarded-access-token";
        both.Request.Headers["X-NyxID-Delegation-Token"] = "delegation-token";
        Extract(both).Should().Be("forwarded-access-token");

        var identityOnly = new DefaultHttpContext();
        identityOnly.Request.Headers["X-NyxID-Identity-Token"] = "identity-assertion";
        Extract(identityOnly).Should().BeNull();

        var malformedBearer = new DefaultHttpContext();
        malformedBearer.Request.Headers.Authorization = "Basic invalid";
        malformedBearer.Request.Headers["X-NyxID-Delegation-Token"] = "delegation-token";
        Extract(malformedBearer).Should().BeNull();
    }

    [Fact]
    public void ExtractCredentials_DelegatedBrowserForward_ShouldPreserveExecutionAndInventoryTokens()
    {
        var executionToken = BuildJwt(
            "{\"delegated\":true,\"act\":{\"sub\":\"aevatar\"},\"scope\":\"proxy\"}");
        var inventoryToken = BuildJwt(
            "{\"delegated\":true,\"act\":{\"sub\":\"aevatar\"},\"scope\":\"account:read\"}");
        var browserRequest = new DefaultHttpContext();
        browserRequest.Request.Headers.Authorization = $"Bearer {executionToken}";
        browserRequest.Request.Headers["X-NyxID-Delegation-Token"] = inventoryToken;

        var credentials = (AgentToolCredentials?)ExtractCredentialsMethod.Invoke(null, [browserRequest]);

        credentials.Should().NotBeNull();
        credentials!.NyxIdCredentialKind.Should().Be(AgentToolNyxIdCredentialKind.ProxyDelegation);
        credentials.NyxIdAccessToken.Should().Be(executionToken);
        credentials.SourceReadableNyxIdAccessToken.Should().Be(inventoryToken);
        AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(credentials)
            .Should().Be(inventoryToken);
    }

    private static string? Extract(HttpContext http) =>
        (string?)ExtractAccessTokenMethod.Invoke(null, [http]);

    private static string BuildJwt(string payloadJson)
    {
        var header = Base64UrlEncode("{\"alg\":\"none\"}");
        var payload = Base64UrlEncode(payloadJson);
        return $"{header}.{payload}.";
    }

    private static string Base64UrlEncode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
