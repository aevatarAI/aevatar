using System.Security.Cryptography;
using Aevatar.Authentication.Hosting;
using Aevatar.Authentication.ScopeServiceTokens;
using Aevatar.Capabilities;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.Bootstrap.Tests;

public sealed class ScopeServiceTokenAuthenticationTests
{
    [Fact]
    public void AddAevatarAuthentication_WhenScopeServiceTokensEnabled_ShouldAcceptConfiguredSelfIssuer()
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

        jwtOptions.TokenValidationParameters.ValidIssuers.Should().Contain("https://nyxid.example.com/");
        jwtOptions.TokenValidationParameters.ValidIssuers.Should().Contain("https://nyxid.example.com");
        jwtOptions.TokenValidationParameters.ValidIssuers.Should().Contain("https://aevatar.example.com");
        jwtOptions.TokenValidationParameters.IssuerSigningKeys.Should().ContainSingle()
            .Which.KeyId.Should().Be("scope-kid-1");
        jwtOptions.TokenValidationParameters.IssuerSigningKeyResolver.Should().BeNull();
        jwtOptions.TokenValidationParameters.ValidAudiences.Should().Contain("aevatar-scope-services");
    }

    [Fact]
    public void ScopeServiceTokenKeyProvider_WhenRsaConfigured_ShouldExposePublicOnlyValidationKey()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        using var keyProvider = new ConfiguredScopeServiceTokenKeyProvider(Options.Create(new ScopeServiceTokenOptions
        {
            Enabled = true,
            Issuer = "https://aevatar.example.com",
            Audience = "aevatar-scope-services",
            SigningKeys =
            [
                new ScopeServiceTokenSigningKeyOptions
                {
                    Kid = "scope-rsa-kid-1",
                    Algorithm = ScopeServiceTokenAlgorithms.RsaSha256,
                    Pem = pem,
                    Current = true,
                },
            ],
        }));

        var signingKey = keyProvider.CurrentSigningKey.SigningKey.Should().BeOfType<RsaSecurityKey>().Subject;
        signingKey.Rsa!.ExportParameters(true).D.Should().NotBeNull();

        var validationKey = keyProvider.CurrentSigningKey.ValidationKey.Should().BeOfType<RsaSecurityKey>().Subject;
        var publicParameters = validationKey.Rsa!.ExportParameters(false);
        publicParameters.Modulus.Should().NotBeNull();
        validationKey.Invoking(key => key.Rsa!.ExportParameters(true))
            .Should().Throw<CryptographicException>();
    }

    [Fact]
    public void ScopeServiceTokenIssuer_ShouldMintScopeClaimAcceptedByScopeGuard()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new HostEnvironmentStub(Environments.Production));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:Authentication:Enabled"] = "true",
            })
            .Build());
        using var serviceProvider = services.BuildServiceProvider();

        var options = Options.Create(new ScopeServiceTokenOptions
        {
            Enabled = true,
            Issuer = "https://aevatar.example.com",
            Audience = "aevatar-scope-services",
            SigningKeys =
            [
                new ScopeServiceTokenSigningKeyOptions
                {
                    Kid = "scope-kid-1",
                    Algorithm = ScopeServiceTokenAlgorithms.HmacSha256,
                    Key = "0123456789abcdef0123456789abcdef",
                    Current = true,
                },
            ],
        });
        using var keyProvider = new ConfiguredScopeServiceTokenKeyProvider(options);
        var issuer = new ScopeServiceTokenIssuer(keyProvider, options);

        var token = issuer.Issue(new ScopeServiceTokenRequest(
            ScopeId: "scope-a",
            ServiceId: "orders",
            ServiceKey: "scope-a:default:default:orders"));
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var principal = handler.ValidateToken(token.AccessToken, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = keyProvider.Issuer,
            ValidateAudience = true,
            ValidAudience = keyProvider.Audience,
            ValidateLifetime = true,
            IssuerSigningKey = keyProvider.CurrentSigningKey.ValidationKey,
            ClockSkew = TimeSpan.FromSeconds(5),
        }, out _);
        var http = new DefaultHttpContext
        {
            RequestServices = serviceProvider,
            User = principal,
        };

        AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, "scope-a", out _)
            .Should().BeFalse();
        AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, "scope-b", out _)
            .Should().BeTrue();
        token.Kid.Should().Be("scope-kid-1");
        token.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    private sealed class HostEnvironmentStub(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
