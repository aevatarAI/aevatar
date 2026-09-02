using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Aevatar.Studio.Tests;

public sealed class AppScopeResolverExtensionsTests
{
    [Fact]
    public void ResolveScopeIdOrDefault_WhenNoHttpRequestExists_ShouldReturnDefault()
    {
        var resolver = CreateResolver(httpContext: null);

        var scopeId = resolver.ResolveScopeIdOrDefault();

        scopeId.Should().Be("default");
    }

    [Fact]
    public void ResolveScopeIdOrDefault_WhenUnauthenticatedHttpRequestHasNoScope_ShouldThrowResourceNeutralError()
    {
        var resolver = CreateResolver(new DefaultHttpContext());

        var act = resolver.ResolveScopeIdOrDefault;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("HTTP request has no resolvable scope; refusing to use the default scope.");
    }

    private static DefaultAppScopeResolver CreateResolver(HttpContext? httpContext) =>
        new(
            new HttpContextAccessor { HttpContext = httpContext },
            new ConfigurationBuilder().Build());
}
