using System.Security.Claims;
using System.Text.Encodings.Web;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting.Auth;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Tests;

public sealed class StudioAuthMeEndpointTests
{
    [Fact]
    public async Task HandleGetAuthMeAsync_WithHostedPrincipal_ShouldReturnProviderProfileAndKeepScopeSeparate()
    {
        var services = new ServiceCollection();
        services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, PassiveAuthenticationHandler>(
            "test",
            _ => { });
        services.AddSingleton<IAppScopeResolver>(new FixedScopeResolver("scope-uuid-1", "claim:scope_id"));
        services.AddSingleton<IAppAuthProfileResolver>(new FixedProfileResolver(new AppAuthProfileResponse(
            Subject: "nyx-user-1",
            Name: "Abigail Deng",
            Email: "abigail@example.com",
            EmailVerified: true,
            Picture: "https://example.com/avatar.png",
            Roles: ["user"],
            Groups: ["studio"])));
        await using var provider = services.BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("scope_id", "scope-uuid-1"),
                new Claim("exp", "1893456000"),
            ], "test")),
        };
        http.Request.Headers.Authorization = "Bearer hosted-token";

        var response = await StudioEndpoints.HandleGetAuthMeAsync(http, CancellationToken.None);

        response.Authenticated.Should().BeTrue();
        response.Name.Should().Be("Abigail Deng");
        response.Email.Should().Be("abigail@example.com");
        response.Profile.Should().BeEquivalentTo(new
        {
            Subject = "nyx-user-1",
            Name = "Abigail Deng",
            Email = "abigail@example.com",
            EmailVerified = true,
            Picture = "https://example.com/avatar.png",
            Roles = new[] { "user" },
            Groups = new[] { "studio" },
        });
        response.ScopeId.Should().Be("scope-uuid-1");
        response.Session.ScopeId.Should().Be("scope-uuid-1");
        response.Profile!.Subject.Should().NotBe(response.ScopeId);
        response.Profile.Name.Should().NotBe(response.ScopeId);
    }

    private sealed class FixedScopeResolver(string scopeId, string source) : IAppScopeResolver
    {
        public AppScopeContext? Resolve(HttpContext? httpContext = null) => new(scopeId, source);

        public bool HasHttpRequestContext(HttpContext? httpContext = null) => true;

        public bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null) => false;
    }

    private sealed class FixedProfileResolver(AppAuthProfileResponse profile) : IAppAuthProfileResolver
    {
        public Task<AppAuthProfileResponse?> ResolveAsync(
            HttpContext http,
            AppAuthProfileResponse? claimsProfile,
            CancellationToken ct) =>
            Task.FromResult<AppAuthProfileResponse?>(profile);
    }

    private sealed class PassiveAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}
