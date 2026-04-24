using System.Security.Claims;
using Aevatar.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Studio.Tests;

public sealed class ScopeEndpointAccessTests
{
    private static HttpContext CreateContext(bool authenticated, params Claim[] claims) =>
        CreateContext(authenticated, configuredAuthenticationEnabled: null, includeEnvironment: true, claims);

    private static HttpContext CreateContext(
        bool authenticated,
        string? configuredAuthenticationEnabled = null,
        bool includeEnvironment = true,
        params Claim[] claims)
    {
        var identity = authenticated
            ? new ClaimsIdentity(claims, "test")
            : new ClaimsIdentity(claims);
        var principal = new ClaimsPrincipal(identity);
        var settings = configuredAuthenticationEnabled is null
            ? null
            : new Dictionary<string, string?> { ["Aevatar:Authentication:Enabled"] = configuredAuthenticationEnabled };
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(settings ?? [])
                .Build());
        if (includeEnvironment)
            services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());

        var context = new DefaultHttpContext
        {
            User = principal,
            RequestServices = services.BuildServiceProvider(),
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
        AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, "scope-1", out _)
            .Should().BeTrue();
    }

    [Fact]
    public void TryCreateScopeAccessDeniedResult_ShouldDeny_WhenNoScopeClaim()
    {
        var http = CreateContext(true, new Claim("role", "admin"));
        AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, "scope-1", out _)
            .Should().BeTrue();
    }

    [Fact]
    public void TryCreateScopeAccessDeniedResult_ShouldDeny_WhenAmbiguousScope()
    {
        var http = CreateContext(true,
            new Claim("scope_id", "scope-1"),
            new Claim("scope_id", "scope-2"));
        AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, "scope-1", out _)
            .Should().BeTrue();
    }

    [Fact]
    public void TryCreateScopeAccessDeniedResult_ShouldDeny_WhenScopeMismatch()
    {
        var http = CreateContext(true, new Claim("scope_id", "scope-other"));
        AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, "scope-1", out _)
            .Should().BeTrue();
    }

    [Fact]
    public void TryCreateScopeAccessDeniedResult_ShouldAllow_WhenScopeMatches()
    {
        var http = CreateContext(true, new Claim("scope_id", "scope-1"));
        AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, "scope-1", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryCreateScopeAccessDeniedResult_ShouldMatchTrimmed()
    {
        var http = CreateContext(true, new Claim("scope_id", "  scope-1  "));
        AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, " scope-1 ", out _)
            .Should().BeFalse();
    }

    [Fact]
    public async Task TryWriteScopeAccessDeniedAsync_ShouldDeny_WhenNotAuthenticated()
    {
        var http = CreateContext(false);
        var denied = await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, "scope-1", CancellationToken.None);
        denied.Should().BeTrue();
        http.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task TryWriteScopeAccessDeniedAsync_ShouldAllow_WhenScopeMatches()
    {
        var http = CreateContext(true, new Claim("scope_id", "scope-1"));
        var denied = await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, "scope-1", CancellationToken.None);
        denied.Should().BeFalse();
    }

    [Fact]
    public void TryCreateScopeAccessDeniedResult_ShouldThrow_WhenAuthDisabledButEnvironmentMissing()
    {
        var http = CreateContext(
            authenticated: true,
            configuredAuthenticationEnabled: "false",
            includeEnvironment: false,
            claims: new Claim("scope_id", "scope-1"));

        var act = () => AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, "scope-1", out _);

        act.Should().Throw<ArgumentNullException>();
    }
}
