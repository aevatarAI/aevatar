using System.Security.Claims;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Aevatar.Tools.Cli.Tests;

public sealed class AppScopeResolverTests
{
    [Fact]
    public void Resolve_ShouldPreferExplicitScopeClaimOverSub()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", "oidc-sub-1"),
                    new Claim("scope_id", "nyx-scope-1"),
                ],
                authenticationType: "test")),
        };
        var resolver = CreateResolver();

        var scope = resolver.Resolve(httpContext);

        scope.Should().NotBeNull();
        scope!.ScopeId.Should().Be("nyx-scope-1");
        scope.Source.Should().Be("claim:scope_id");
    }

    [Fact]
    public void Resolve_ShouldFallBackToConfiguredScope()
    {
        var resolver = CreateResolver(new Dictionary<string, string?>
        {
            ["Cli:App:ScopeId"] = "configured-scope",
        });

        var scope = resolver.Resolve();

        scope.Should().NotBeNull();
        scope!.ScopeId.Should().Be("configured-scope");
        scope.Source.Should().Be("config:Cli:App:ScopeId");
    }

    [Fact]
    public void Resolve_ShouldNotUseHeaderWhenNyxIdAuthIsEnabled()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Aevatar-Scope-Id"] = "header-scope";
        var resolver = CreateResolver();

        var scope = resolver.Resolve(httpContext);

        scope.Should().BeNull();
    }

    [Fact]
    public void Resolve_ShouldUseHeaderWhenNyxIdAuthIsDisabled()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Aevatar-Scope-Id"] = "header-scope";
        var resolver = CreateResolver(new Dictionary<string, string?>
        {
            ["Cli:App:NyxId:Enabled"] = "false",
        });

        var scope = resolver.Resolve(httpContext);

        scope.Should().NotBeNull();
        scope!.ScopeId.Should().Be("header-scope");
        scope.Source.Should().Be("header:X-Aevatar-Scope-Id");
    }

    private static DefaultAppScopeResolver CreateResolver(
        IReadOnlyDictionary<string, string?>? configurationValues = null)
    {
        var accessor = new HttpContextAccessor();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues ?? new Dictionary<string, string?>())
            .Build();

        return new DefaultAppScopeResolver(accessor, configuration);
    }
}
