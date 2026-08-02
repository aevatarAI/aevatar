using System.Reflection;
using Aevatar.Authentication.Abstractions;
using Aevatar.Authentication.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.Bootstrap.Tests;

public sealed class JwtAlgorithmPinningTests
{
    [Fact]
    public void ResolveValidAlgorithms_WhenScopeTokensDisabled_ShouldPinAsymmetricOidcSupersetWithoutSymmetric()
    {
        var algorithms = InvokeResolveValidAlgorithms(new AevatarAuthenticationOptions(), scopeServiceTokensEnabled: false);

        algorithms.Should().Contain(new[] { "RS256", "RS384", "RS512", "ES256", "ES384", "PS256" });
        algorithms.Should().NotContain("HS256");
        algorithms.Should().NotContain("none");
    }

    [Fact]
    public void ResolveValidAlgorithms_WhenScopeTokensEnabled_ShouldAlsoAllowScopeTokenHs256AndRs256()
    {
        var algorithms = InvokeResolveValidAlgorithms(new AevatarAuthenticationOptions(), scopeServiceTokensEnabled: true);

        algorithms.Should().Contain("HS256");
        algorithms.Should().Contain("RS256");
        algorithms.Should().Contain("ES256");
        algorithms.Should().NotContain("none");
    }

    [Fact]
    public void ResolveValidAlgorithms_WhenExplicitlyConfigured_ShouldUseTheConfiguredOverride()
    {
        var options = new AevatarAuthenticationOptions { ValidAlgorithms = ["RS256"] };

        var algorithms = InvokeResolveValidAlgorithms(options, scopeServiceTokensEnabled: true);

        algorithms.Should().Equal("RS256");
    }

    [Fact]
    public void AddAevatarAuthentication_WhenEnabled_ShouldPinValidAlgorithmsOnBearerOptions()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.Configuration["Aevatar:Authentication:Enabled"] = "true";
        builder.Configuration["Aevatar:Authentication:Authority"] = "https://id.example.com";

        builder.AddAevatarAuthentication();
        using var app = builder.Build();

        using var scope = app.Services.CreateScope();
        var jwtOptions = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        jwtOptions.TokenValidationParameters.ValidAlgorithms.Should().NotBeNull();
        jwtOptions.TokenValidationParameters.ValidAlgorithms.Should().Contain("RS256");
        jwtOptions.TokenValidationParameters.ValidAlgorithms.Should().Contain("ES256");
        jwtOptions.TokenValidationParameters.ValidAlgorithms.Should().NotContain("none");
    }

    [Fact]
    public void AddAevatarAuthentication_WhenScopeServiceTokensEnabled_ShouldIncludeHs256InPinnedAlgorithms()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.Configuration["Aevatar:Authentication:Enabled"] = "true";
        builder.Configuration["Aevatar:Authentication:Authority"] = "https://nyxid.example.com/";
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:Enabled"] = "true";
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:Issuer"] = "https://aevatar.example.com";
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:Audience"] = "aevatar-scope-services";
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:SigningKeys:0:Kid"] = "scope-kid-1";
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:SigningKeys:0:Algorithm"] = "HS256";
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:SigningKeys:0:Key"] =
            "0123456789abcdef0123456789abcdef";

        builder.AddAevatarAuthentication();
        using var app = builder.Build();

        using var scope = app.Services.CreateScope();
        var jwtOptions = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        jwtOptions.TokenValidationParameters.ValidAlgorithms.Should().Contain("HS256");
        jwtOptions.TokenValidationParameters.ValidAlgorithms.Should().Contain("RS256");
    }

    private static string[] InvokeResolveValidAlgorithms(
        AevatarAuthenticationOptions options,
        bool scopeServiceTokensEnabled)
    {
        var method = typeof(AevatarAuthenticationHostExtensions).GetMethod(
            "ResolveValidAlgorithms",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ResolveValidAlgorithms not found.");

        return (string[])method.Invoke(null, [options, scopeServiceTokensEnabled])!;
    }
}
