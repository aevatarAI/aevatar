using System.Reflection;
using System.Security.Cryptography;
using Aevatar.Authentication.Hosting;
using Aevatar.Authentication.ScopeServiceTokens;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.Bootstrap.Tests;

public sealed class PerIssuerSigningKeyResolverTests
{
    private const string ScopeIssuer = "https://aevatar.example.com";
    private const string LoginAuthority = "https://login.nyxid.example.com";
    private const string DiscoveredIssuer = "https://nyx-api.example.com";

    [Fact]
    public void Resolve_WhenIssuerIsScopeIssuer_ShouldReturnOnlyScopeKeys()
    {
        var scopeKey = NamedSymmetricKey("scope-key");
        var oidcKey = NamedSymmetricKey("oidc-key");
        var resolver = CreateResolver([LoginAuthority], [ScopeIssuer], [scopeKey]);

        var resolved = InvokeResolve(resolver, ScopeIssuer, discoveryKeys: [oidcKey]).ToList();

        resolved.Should().ContainSingle().Which.KeyId.Should().Be("scope-key");
        resolved.Should().NotContain(key => key.KeyId == "oidc-key");
    }

    [Fact]
    public void Resolve_WhenIssuerIsDiscoveredIssuer_ShouldReturnOnlyDiscoveryKeys()
    {
        var scopeKey = NamedSymmetricKey("scope-key");
        var oidcKey = NamedSymmetricKey("oidc-key");
        var resolver = CreateResolver([LoginAuthority], [ScopeIssuer], [scopeKey]);

        var resolved = InvokeResolve(resolver, DiscoveredIssuer, discoveryKeys: [oidcKey]).ToList();

        resolved.Should().ContainSingle().Which.KeyId.Should().Be("oidc-key");
        resolved.Should().NotContain(key => key.KeyId == "scope-key");
    }

    [Fact]
    public void Resolve_WhenIssuerIsUnknown_ShouldFailClosed()
    {
        var scopeKey = NamedSymmetricKey("scope-key");
        var oidcKey = NamedSymmetricKey("oidc-key");
        var resolver = CreateResolver([LoginAuthority], [ScopeIssuer], [scopeKey]);

        var resolved = InvokeResolve(
            resolver,
            tokenIssuer: "https://unknown.example.com",
            discoveryKeys: [oidcKey]).ToList();

        resolved.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_WhenIssuerIsLoginAuthorityButDiscoveryUsesDifferentIssuer_ShouldFailClosed()
    {
        var scopeKey = NamedSymmetricKey("scope-key");
        var oidcKey = NamedSymmetricKey("oidc-key");
        var resolver = CreateResolver([LoginAuthority], [ScopeIssuer], [scopeKey]);

        var resolved = InvokeResolve(resolver, LoginAuthority, discoveryKeys: [oidcKey]).ToList();

        resolved.Should().BeEmpty();
    }

    [Fact]
    public void ValidateIssuer_WhenIssuerMatchesDiscovery_ShouldReturnDiscoveredIssuer()
    {
        var resolver = CreateResolver(
            [LoginAuthority],
            [ScopeIssuer],
            [NamedSymmetricKey("scope-key")]);

        var validated = InvokeValidateIssuer(resolver, DiscoveredIssuer);

        validated.Should().Be(DiscoveredIssuer);
    }

    [Fact]
    public void ValidateIssuer_WhenIssuerMatchesOnlyLoginAuthority_ShouldFailClosed()
    {
        var resolver = CreateResolver(
            [LoginAuthority],
            [ScopeIssuer],
            [NamedSymmetricKey("scope-key")]);

        var act = () => InvokeValidateIssuer(resolver, LoginAuthority);

        act.Should().Throw<TargetInvocationException>()
            .Which.InnerException.Should().BeOfType<SecurityTokenInvalidIssuerException>();
    }

    [Fact]
    public void ResolveAndValidateIssuer_WhenScopeIssuerEqualsDiscoveredIssuer_ShouldFailClosed()
    {
        var scopeKey = NamedSymmetricKey("scope-key");
        var resolver = CreateResolver([LoginAuthority], [DiscoveredIssuer], [scopeKey]);

        var resolved = InvokeResolve(
            resolver,
            DiscoveredIssuer,
            discoveryKeys: [NamedSymmetricKey("oidc-key")]).ToList();
        var validate = () => InvokeValidateIssuer(resolver, DiscoveredIssuer);

        resolved.Should().BeEmpty();
        validate.Should().Throw<TargetInvocationException>()
            .Which.InnerException.Should().BeOfType<SecurityTokenInvalidIssuerException>();
    }

    [Fact]
    public void AddAevatarAuthentication_WhenScopeServiceTokensEnabled_ShouldInstallIssuerSigningKeyResolver()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.Configuration["Aevatar:Authentication:Enabled"] = "true";
        builder.Configuration["Aevatar:Authentication:Authority"] = "https://nyxid.example.com/";
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:Enabled"] = "true";
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:Issuer"] = ScopeIssuer;
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

        jwtOptions.TokenValidationParameters.IssuerSigningKeyResolver.Should().BeNull();
        jwtOptions.TokenValidationParameters.IssuerSigningKeyResolverUsingConfiguration.Should().NotBeNull();
        jwtOptions.TokenValidationParameters.IssuerValidator.Should().BeNull();
        jwtOptions.TokenValidationParameters.IssuerValidatorUsingConfiguration.Should().NotBeNull();
        jwtOptions.TokenValidationParameters.ValidateIssuer.Should().BeTrue();

        // The installed resolver returns the scope key for a token issued by the scope issuer.
        var scopeKeyId = jwtOptions.TokenValidationParameters.IssuerSigningKeys.Single().KeyId;
        var scopeToken = TokenWithIssuer(ScopeIssuer);
        var resolvedForScopeIssuer = jwtOptions.TokenValidationParameters.IssuerSigningKeyResolverUsingConfiguration!(
            token: string.Empty,
            securityToken: scopeToken,
            kid: "scope-kid-1",
            validationParameters: jwtOptions.TokenValidationParameters,
            configuration: new OpenIdConnectConfiguration()).ToList();
        resolvedForScopeIssuer.Should().ContainSingle().Which.KeyId.Should().Be(scopeKeyId);

        // Unknown issuers never receive either issuer's signing keys.
        var foreignToken = TokenWithIssuer("https://unknown.example.com");
        var resolvedForUnknownIssuer = jwtOptions.TokenValidationParameters.IssuerSigningKeyResolverUsingConfiguration!(
            token: string.Empty,
            securityToken: foreignToken,
            kid: "scope-kid-1",
            validationParameters: jwtOptions.TokenValidationParameters,
            configuration: new OpenIdConnectConfiguration()).ToList();
        resolvedForUnknownIssuer.Should().BeEmpty();
    }

    private static object CreateResolver(
        string[] authorityIssuers,
        string[] scopeIssuers,
        SecurityKey[] scopeKeys)
    {
        var type = ResolverType;
        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single();
        return ctor.Invoke([authorityIssuers, scopeIssuers, scopeKeys]);
    }

    private static IEnumerable<SecurityKey> InvokeResolve(
        object resolver,
        string tokenIssuer,
        SecurityKey[] discoveryKeys)
    {
        var method = ResolverType.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Resolve not found.");
        var parameters = new TokenValidationParameters();
        var configuration = new OpenIdConnectConfiguration { Issuer = DiscoveredIssuer };
        foreach (var key in discoveryKeys)
            configuration.SigningKeys.Add(key);

        // The delegate's issuer comes from securityToken.Issuer (its 3rd arg is the kid), so the
        // issuer under test is carried on a JwtSecurityToken. An empty issuer means "no token".
        SecurityToken? securityToken = string.IsNullOrEmpty(tokenIssuer)
            ? null
            : TokenWithIssuer(tokenIssuer);

        return (IEnumerable<SecurityKey>)method.Invoke(
            resolver,
            [string.Empty, securityToken!, "kid-hint", parameters, configuration])!;
    }

    private static string InvokeValidateIssuer(object resolver, string issuer)
    {
        var method = ResolverType.GetMethod("ValidateIssuer", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ValidateIssuer not found.");
        var parameters = new TokenValidationParameters();
        var configuration = new OpenIdConnectConfiguration { Issuer = DiscoveredIssuer };

        return (string)method.Invoke(
            resolver,
            [issuer, TokenWithIssuer(issuer), parameters, configuration])!;
    }

    private static Type ResolverType =>
        typeof(AevatarAuthenticationHostExtensions).Assembly.GetType(
            "Aevatar.Authentication.Hosting.PerIssuerSigningKeyResolver", throwOnError: true)!;

    private static SymmetricSecurityKey NamedSymmetricKey(string kid) =>
        new(RandomNumberGenerator.GetBytes(32)) { KeyId = kid };

    private static SecurityToken TokenWithIssuer(string issuer) =>
        new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: issuer,
            audience: null,
            claims: null,
            notBefore: null,
            expires: null,
            signingCredentials: null);
}
