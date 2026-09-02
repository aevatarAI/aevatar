using System.Security.Cryptography;
using Aevatar.Authentication.Hosting;
using Aevatar.Authentication.ScopeServiceTokens;
using Aevatar.Bootstrap.Hosting;
using Aevatar.Capabilities;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
        builder.Configuration["Aevatar:Authentication:Audience"] = "aevatar-api";
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
        jwtOptions.TokenValidationParameters.IssuerSigningKeyResolverUsingConfiguration.Should().NotBeNull();
        jwtOptions.TokenValidationParameters.ValidAudience.Should().BeNull();
        jwtOptions.TokenValidationParameters.ValidAudiences.Should().BeNull();
        jwtOptions.TokenValidationParameters.AudienceValidator.Should().NotBeNull();
    }

    [Fact]
    public void AddAevatarAuthentication_WhenOnlyScopeAudienceIsConfiguredOutsideDevelopment_ShouldFailClosed()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });

        builder.Configuration["Aevatar:Authentication:Enabled"] = "true";
        builder.Configuration["Aevatar:Authentication:Authority"] = "https://nyxid.example.com/";
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:Enabled"] = "true";
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:Issuer"] =
            "https://aevatar.example.com";
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:Audience"] =
            "aevatar-scope-services";

        var act = () => builder.AddAevatarAuthentication();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage(
                "Aevatar:Authentication:Audience is required when authentication is enabled outside Development.");
    }

    [Fact]
    public void AddAevatarAuthentication_WhenScopeAudienceIsMissing_ShouldFailClosed()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });

        builder.Configuration["Aevatar:Authentication:Enabled"] = "true";
        builder.Configuration["Aevatar:Authentication:Authority"] = "https://nyxid.example.com/";
        builder.Configuration["Aevatar:Authentication:Audience"] = "aevatar-api";
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:Enabled"] = "true";

        var act = () => builder.AddAevatarAuthentication();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage(
                "Aevatar:Authentication:ScopeServiceTokens:Audience is required when scope service tokens are enabled.");
    }

    [Fact]
    public void UseAevatarDefaultHost_WhenScopeServiceTokensEnabled_ShouldNotExposeJwksRoute()
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
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:JwksPath"] = "/custom-jwks.json";
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:SigningKeys:0:Kid"] = "scope-kid-1";
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:SigningKeys:0:Algorithm"] = "HS256";
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:SigningKeys:0:Key"] =
            "0123456789abcdef0123456789abcdef";
        builder.AddAevatarDefaultHost(options =>
        {
            options.AllowLocalFileSecretsStore = false;
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
            options.EnableOpenApiDocument = false;
            options.AutoMapCapabilities = false;
        });
        builder.AddAevatarAuthentication();

        using var app = builder.Build();
        app.UseAevatarDefaultHost();

        var routeEndpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .ToList();

        routeEndpoints.Should().Contain("/");
        routeEndpoints.Should().Contain("/health/live");
        routeEndpoints.Should().NotContain("/.well-known/aevatar-scope-service-jwks.json");
        routeEndpoints.Should().NotContain("/custom-jwks.json");
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
    public void ScopeServiceTokenKeyProvider_WhenLegacySingleHmacKeyConfigured_ShouldUseItAsCurrentKey()
    {
        var options = Options.Create(new ScopeServiceTokenOptions
        {
            Issuer = " https://aevatar.example.com ",
            Audience = " ",
            ClockSkewSeconds = -10,
            SigningKey = new ScopeServiceTokenSigningKeyOptions
            {
                Kid = " scope-hmac-kid-1 ",
                Algorithm = "hmac_sha256",
                KeyBase64 = Convert.ToBase64String("0123456789abcdef0123456789abcdef"u8.ToArray()),
            },
        });

        using var keyProvider = new ConfiguredScopeServiceTokenKeyProvider(options);

        keyProvider.Issuer.Should().Be("https://aevatar.example.com");
        keyProvider.Audience.Should().BeNull();
        keyProvider.ClockSkew.Should().Be(TimeSpan.Zero);
        keyProvider.CurrentSigningKey.Kid.Should().Be("scope-hmac-kid-1");
        keyProvider.CurrentSigningKey.Algorithm.Should().Be(SecurityAlgorithms.HmacSha256);
        keyProvider.CurrentSigningKey.SigningKey.Should().BeSameAs(keyProvider.CurrentSigningKey.ValidationKey);
        keyProvider.ValidationKeys.Should().ContainSingle();
    }

    [Fact]
    public void ScopeServiceTokenKeyProvider_WhenMultipleKeysConfigured_ShouldSelectMarkedCurrentKey()
    {
        using var keyProvider = new ConfiguredScopeServiceTokenKeyProvider(Options.Create(new ScopeServiceTokenOptions
        {
            Issuer = "https://aevatar.example.com",
            SigningKeys =
            [
                new ScopeServiceTokenSigningKeyOptions
                {
                    Kid = "scope-old",
                    Algorithm = "HS256",
                    Key = "0123456789abcdef0123456789abcdef",
                },
                new ScopeServiceTokenSigningKeyOptions
                {
                    Kid = "scope-current",
                    Algorithm = "HS256",
                    Key = "abcdef0123456789abcdef0123456789",
                    Current = true,
                },
            ],
        }));

        keyProvider.CurrentSigningKey.Kid.Should().Be("scope-current");
        keyProvider.ValidationKeys.Select(key => key.Kid).Should().ContainInOrder("scope-old", "scope-current");
    }

    [Fact]
    public void ScopeServiceTokenKeyProvider_WhenMultipleCurrentKeysConfigured_ShouldFailFast()
    {
        var act = () => new ConfiguredScopeServiceTokenKeyProvider(Options.Create(new ScopeServiceTokenOptions
        {
            Issuer = "https://aevatar.example.com",
            SigningKeys =
            [
                new ScopeServiceTokenSigningKeyOptions
                {
                    Kid = "scope-a",
                    Algorithm = "HS256",
                    Key = "0123456789abcdef0123456789abcdef",
                    Current = true,
                },
                new ScopeServiceTokenSigningKeyOptions
                {
                    Kid = "scope-b",
                    Algorithm = "HS256",
                    Key = "abcdef0123456789abcdef0123456789",
                    Current = true,
                },
            ],
        }));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Only one scope service token signing key can be marked current.");
    }

    [Fact]
    public void ScopeServiceTokenKeyProvider_WhenHmacKeyIsTooShort_ShouldRejectIt()
    {
        var act = () => new ConfiguredScopeServiceTokenKeyProvider(Options.Create(new ScopeServiceTokenOptions
        {
            Issuer = "https://aevatar.example.com",
            SigningKey = new ScopeServiceTokenSigningKeyOptions
            {
                Kid = "short",
                Algorithm = "HS256",
                Key = "too-short",
            },
        }));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Scope service token HS256 signing key must be at least 32 bytes.");
    }

    [Fact]
    public void ScopeServiceTokenKeyProvider_WhenUnsupportedAlgorithmConfigured_ShouldRejectIt()
    {
        var act = () => new ConfiguredScopeServiceTokenKeyProvider(Options.Create(new ScopeServiceTokenOptions
        {
            Issuer = "https://aevatar.example.com",
            SigningKey = new ScopeServiceTokenSigningKeyOptions
            {
                Kid = "scope-key",
                Algorithm = "ES256",
                Key = "0123456789abcdef0123456789abcdef",
            },
        }));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Unsupported scope service token signing algorithm 'ES256'.");
    }

    [Fact]
    public void ScopeServiceTokenKeyProvider_WhenRsaPemPathConfigured_ShouldLoadPrivateKeyFromFile()
    {
        using var rsa = RSA.Create(2048);
        var path = Path.Combine(Path.GetTempPath(), $"scope-service-token-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
        try
        {
            using var keyProvider = new ConfiguredScopeServiceTokenKeyProvider(Options.Create(new ScopeServiceTokenOptions
            {
                Issuer = "https://aevatar.example.com",
                SigningKey = new ScopeServiceTokenSigningKeyOptions
                {
                    Kid = "scope-rsa-path",
                    Algorithm = "rsa_sha256",
                    PemPath = path,
                },
            }));

            keyProvider.CurrentSigningKey.Kid.Should().Be("scope-rsa-path");
            keyProvider.CurrentSigningKey.Algorithm.Should().Be(SecurityAlgorithms.RsaSha256);
            keyProvider.CurrentSigningKey.ValidationKey.Should().BeOfType<RsaSecurityKey>();
        }
        finally
        {
            File.Delete(path);
        }
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
