using System.Security.Claims;
using Aevatar.GAgentService.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Studio.Tests;

public sealed class ScopeEndpointAccessTests
{
    private static HttpContext CreateContext(bool authenticated, params Claim[] claims)
    {
        var identity = authenticated
            ? new ClaimsIdentity(claims, "test")
            : new ClaimsIdentity(claims);
        var principal = new ClaimsPrincipal(identity);
        var context = new DefaultHttpContext { User = principal };
        return context;
    }

    [Fact]
    public void TryCreateScopeAccessDeniedResult_ShouldDeny_WhenNotAuthenticated()
    {
        var http = CreateContext(false);
        ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, "scope-1", out _)
            .Should().BeTrue();
    }

    [Fact]
    public void TryCreateScopeAccessDeniedResult_ShouldDeny_WhenNoScopeClaim()
    {
        var http = CreateContext(true, new Claim("role", "admin"));
        ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, "scope-1", out _)
            .Should().BeTrue();
    }

    [Fact]
    public void TryCreateScopeAccessDeniedResult_ShouldDeny_WhenAmbiguousScope()
    {
        var http = CreateContext(true,
            new Claim("scope_id", "scope-1"),
            new Claim("scope_id", "scope-2"));
        ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, "scope-1", out _)
            .Should().BeTrue();
    }

    [Fact]
    public void TryCreateScopeAccessDeniedResult_ShouldDeny_WhenScopeMismatch()
    {
        var http = CreateContext(true, new Claim("scope_id", "scope-other"));
        ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, "scope-1", out _)
            .Should().BeTrue();
    }

    [Fact]
    public void TryCreateScopeAccessDeniedResult_ShouldAllow_WhenScopeMatches()
    {
        var http = CreateContext(true, new Claim("scope_id", "scope-1"));
        ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, "scope-1", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryCreateScopeAccessDeniedResult_ShouldMatchTrimmed()
    {
        var http = CreateContext(true, new Claim("scope_id", "  scope-1  "));
        ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, " scope-1 ", out _)
            .Should().BeFalse();
    }

    [Fact]
    public async Task TryWriteScopeAccessDeniedAsync_ShouldDeny_WhenNotAuthenticated()
    {
        var http = CreateContext(false);
        var denied = await ScopeEndpointAccess.TryWriteScopeAccessDeniedAsync(http, "scope-1", CancellationToken.None);
        denied.Should().BeTrue();
        http.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task TryWriteScopeAccessDeniedAsync_ShouldAllow_WhenScopeMatches()
    {
        var http = CreateContext(true, new Claim("scope_id", "scope-1"));
        var denied = await ScopeEndpointAccess.TryWriteScopeAccessDeniedAsync(http, "scope-1", CancellationToken.None);
        denied.Should().BeFalse();
    }
}
