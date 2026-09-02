using Aevatar.Authentication.Abstractions;
using Aevatar.Mainnet.Host.Api.AgentProfiles;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetAgentProfileEndpointAuthorizationTests
{
    [Theory]
    [InlineData(false, PlatformAdminGrantSources.AllowedUserId, "admin-user")]
    [InlineData(true, PlatformAdminGrantSources.AllowedUserId, "")]
    [InlineData(true, PlatformAdminGrantSources.AllowedEmail, "admin-user")]
    [InlineData(true, PlatformAdminGrantSources.NyxIdPlatformRole, "admin-user")]
    public async Task AuthorizeSystemAdminAsync_ShouldRejectAnyGrantOtherThanAllowlistedUserId(
        bool isElevated,
        string grantSource,
        string userId)
    {
        var context = Context();
        var authorizer = new StaticAuthorizer(new PlatformCaller(
            isElevated,
            "admin",
            "admin@example.com",
            userId,
            grantSource));

        var result = await AgentProfileEndpoints.AuthorizeSystemAdminAsync(
            context,
            authorizer,
            CancellationToken.None);

        result.Caller.Should().BeNull();
        result.Error.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>();
        authorizer.BearerTokens.Should().Equal("admin-token");
    }

    [Fact]
    public async Task AuthorizeSystemAdminAsync_ShouldAcceptOnlyAllowlistedUserIdGrant()
    {
        var context = Context();
        var authorizer = new StaticAuthorizer(new PlatformCaller(
            true,
            "admin",
            "admin@example.com",
            "admin-user",
            PlatformAdminGrantSources.AllowedUserId));

        var result = await AgentProfileEndpoints.AuthorizeSystemAdminAsync(
            context,
            authorizer,
            CancellationToken.None);

        result.Error.Should().BeNull();
        result.Caller!.UserId.Should().Be("admin-user");
        result.BearerToken.Should().Be("admin-token");
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer admin-token";
        return context;
    }

    private sealed class StaticAuthorizer(PlatformCaller caller) : IPlatformAdminAuthorizer
    {
        public List<string> BearerTokens { get; } = [];

        public Task<PlatformCaller> ResolveCallerAsync(
            string bearerToken,
            CancellationToken ct = default)
        {
            BearerTokens.Add(bearerToken);
            return Task.FromResult(caller);
        }
    }
}
