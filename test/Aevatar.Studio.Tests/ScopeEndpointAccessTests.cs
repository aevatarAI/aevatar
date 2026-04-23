using System.Security.Claims;
using Aevatar.GAgentService.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Studio.Tests;

public sealed class ScopeEndpointAccessTests
{
    private static HttpContext CreateContext(bool authenticated, params Claim[] claims)
    {
        var identity = authenticated
            ? new ClaimsIdentity(claims, "test")
            : new ClaimsIdentity(claims);
        var principal = new ClaimsPrincipal(identity);
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            User = principal,
            RequestServices = services,
        };
        return context;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.Studio.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
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
