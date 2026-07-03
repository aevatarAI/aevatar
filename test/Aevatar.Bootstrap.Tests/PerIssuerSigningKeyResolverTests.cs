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
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.Bootstrap.Tests;

public sealed class PerIssuerSigningKeyResolverTests
{
    private const string ScopeIssuer = "https://aevatar.example.com";
    private const string OidcIssuer = "https://nyxid.example.com";

    [Fact]
    public void Resolve_WhenIssuerIsScopeIssuer_ShouldReturnOnlyScopeKeys()
    {
        var scopeKey = NamedSymmetricKey("scope-key");
        var oidcKey = NamedSymmetricKey("oidc-key");
        var resolver = CreateResolver([ScopeIssuer], [scopeKey]);

        var resolved = InvokeResolve(resolver, ScopeIssuer, allConfiguredKeys: [scopeKey, oidcKey]).ToList();

        resolved.Should().ContainSingle().Which.KeyId.Should().Be("scope-key");
        resolved.Should().NotContain(key => key.KeyId == "oidc-key");
    }

    [Fact]
    public void Resolve_WhenIssuerIsUnknown_ShouldFallBackToAllConfiguredKeys()
    {
        var scopeKey = NamedSymmetricKey("scope-key");
        var oidcKey = NamedSymmetricKey("oidc-key");
        var resolver = CreateResolver([ScopeIssuer], [scopeKey]);

        var resolved = InvokeResolve(resolver, OidcIssuer, allConfiguredKeys: [oidcKey]).ToList();

        // Non-breaking fallback: unknown issuer keeps every configured key in play (the OIDC key
        // the base handler injected, plus the resolver's own scope keys as a genuine superset).
        resolved.Select(key => key.KeyId).Should().Contain("oidc-key");
        resolved.Select(key => key.KeyId).Should().Contain("scope-key");
    }

    [Fact]
    public void Resolve_WhenIssuerIsEmpty_ShouldFallBackToAllConfiguredKeys()
    {
        var scopeKey = NamedSymmetricKey("scope-key");
        var oidcKey = NamedSymmetricKey("oidc-key");
        var resolver = CreateResolver([ScopeIssuer], [scopeKey]);

        var resolved = InvokeResolve(resolver, tokenIssuer: string.Empty, allConfiguredKeys: [oidcKey]).ToList();

        resolved.Select(key => key.KeyId).Should().Contain("oidc-key");
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

        jwtOptions.TokenValidationParameters.IssuerSigningKeyResolver.Should().NotBeNull();

        // The installed resolver returns the scope key for a token issued by the scope issuer.
        var scopeKeyId = jwtOptions.TokenValidationParameters.IssuerSigningKeys.Single().KeyId;
        var scopeToken = TokenWithIssuer(ScopeIssuer);
        var resolvedForScopeIssuer = jwtOptions.TokenValidationParameters.IssuerSigningKeyResolver!(
            token: string.Empty,
            securityToken: scopeToken,
            kid: "scope-kid-1",
            validationParameters: jwtOptions.TokenValidationParameters).ToList();
        resolvedForScopeIssuer.Should().ContainSingle().Which.KeyId.Should().Be(scopeKeyId);

        // Unknown issuer still yields the configured keys (non-breaking fallback).
        var foreignToken = TokenWithIssuer("https://unknown.example.com");
        var resolvedForUnknownIssuer = jwtOptions.TokenValidationParameters.IssuerSigningKeyResolver!(
            token: string.Empty,
            securityToken: foreignToken,
            kid: "scope-kid-1",
            validationParameters: jwtOptions.TokenValidationParameters).ToList();
        resolvedForUnknownIssuer.Select(key => key.KeyId).Should().Contain(scopeKeyId);
    }

    private static object CreateResolver(string[] scopeIssuers, SecurityKey[] scopeKeys)
    {
        var type = ResolverType;
        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single();
        return ctor.Invoke([scopeIssuers, scopeKeys]);
    }

    private static IEnumerable<SecurityKey> InvokeResolve(
        object resolver,
        string tokenIssuer,
        SecurityKey[] allConfiguredKeys)
    {
        var method = ResolverType.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Resolve not found.");
        var parameters = new TokenValidationParameters { IssuerSigningKeys = allConfiguredKeys };

        // The delegate's issuer comes from securityToken.Issuer (its 3rd arg is the kid), so the
        // issuer under test is carried on a JwtSecurityToken. An empty issuer means "no token".
        SecurityToken? securityToken = string.IsNullOrEmpty(tokenIssuer)
            ? null
            : TokenWithIssuer(tokenIssuer);

        return (IEnumerable<SecurityKey>)method.Invoke(
            resolver,
            [string.Empty, securityToken!, "kid-hint", parameters])!;
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
