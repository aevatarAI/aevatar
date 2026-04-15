using System.Reflection;
using System.Security.Claims;
using Aevatar.Authentication.Abstractions;
using Aevatar.Authentication.Hosting;
using Aevatar.Authentication.Providers.NyxId;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Bootstrap.Tests;

public class AuthenticationHostCoverageTests
{
    [Fact]
    public void AddAevatarAuthentication_WhenEnabled_ShouldRegisterAuthenticationAuthorizationAndClaimsTransformation()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.Configuration["Aevatar:Authentication:Enabled"] = "true";
        builder.Configuration["Aevatar:Authentication:Authority"] = "https://id.example.com";

        builder.AddAevatarAuthentication();
        using var app = builder.Build();

        var provider = app.Services;
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetService<IAuthenticationService>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IAuthorizationService>().Should().NotBeNull();
        scope.ServiceProvider.GetServices<IClaimsTransformation>()
            .Should().ContainSingle(x => x.GetType().Name == "AevatarClaimsTransformation");
    }

    [Fact]
    public void AddAevatarAuthentication_WhenDisabled_ShouldSkipAuthenticationRegistration()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.AddAevatarAuthentication();

        using var app = builder.Build();
        var provider = app.Services;

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetService<IAuthenticationService>().Should().BeNull();
        scope.ServiceProvider.GetServices<IClaimsTransformation>()
            .Should().BeEmpty();
    }

    [Fact]
    public void AddNyxIdAuthentication_ShouldRegisterNyxIdClaimsTransformer()
    {
        var services = new ServiceCollection();

        services.AddNyxIdAuthentication();
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IAevatarClaimsTransformer>()
            .Should().ContainSingle(x => x is NyxIdClaimsTransformer);
    }

    [Fact]
    public void NyxIdClaimsTransformer_ShouldMapScopeIdByPriority_AndExcludeReservedGenericIds()
    {
        var subject = new NyxIdClaimsTransformer();

        Transform(subject, new[] { new Claim(AevatarStandardClaimTypes.ScopeId, "scope-main") })
            .Should().BeEmpty();

        Transform(subject, new[] { new Claim("uid", "uid-1"), new Claim(ClaimTypes.NameIdentifier, "name-1") })
            .Should().ContainSingle(c => c.Type == AevatarStandardClaimTypes.ScopeId && c.Value == "uid-1");

        Transform(subject, new[] { new Claim("sub", "sub-1"), new Claim(ClaimTypes.NameIdentifier, "name-1") })
            .Should().ContainSingle(c => c.Type == AevatarStandardClaimTypes.ScopeId && c.Value == "sub-1");

        Transform(subject, new[] { new Claim(ClaimTypes.NameIdentifier, "name-1") })
            .Should().ContainSingle(c => c.Type == AevatarStandardClaimTypes.ScopeId && c.Value == "name-1");

        Transform(subject, new[]
            {
                new Claim("client_id", "client-1"),
                new Claim("session_id", "session-1"),
                new Claim("order_id", "order-1"),
            })
            .Should().ContainSingle(c => c.Type == AevatarStandardClaimTypes.ScopeId && c.Value == "order-1");
    }

    [Fact]
    public async Task AevatarClaimsTransformation_ShouldSkipDuplicates()
    {
        var transformationType = typeof(AevatarAuthenticationHostExtensions).Assembly.GetType(
            "Aevatar.Authentication.Hosting.AevatarClaimsTransformation", throwOnError: true)!;
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(AevatarStandardClaimTypes.ScopeId, "existing-scope"));
        identity.AddClaim(new Claim("uid", "uid-1"));
        var principal = new ClaimsPrincipal(identity);
        var transformation = Activator.CreateInstance(
            transformationType,
            new object[] { new[] { new NyxIdClaimsTransformer() } })!;

        var method = transformationType.GetMethod("TransformAsync", BindingFlags.Instance | BindingFlags.Public)!;
        method.Should().NotBeNull();
        var result = (Task<ClaimsPrincipal>)method.Invoke(transformation, [principal])!;
        var transformed = await result;

        transformed.FindAll(AevatarStandardClaimTypes.ScopeId)
            .Should().ContainSingle()
            .And.Contain(c => c.Value == "existing-scope");
    }

    private static IReadOnlyList<Claim> Transform(NyxIdClaimsTransformer transformer, IEnumerable<Claim> claims)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims));
        return transformer.TransformClaims(principal).ToList();
    }
}
