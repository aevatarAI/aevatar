using System.Security.Claims;
using Aevatar.Tools.Cli.Hosting;
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

        var scope = resolver.Resolve(new DefaultHttpContext());

        scope.Should().NotBeNull();
        scope!.ScopeId.Should().Be("configured-scope");
        scope.Source.Should().Be("config:Cli:App:ScopeId");
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
