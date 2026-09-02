using Aevatar.Authentication.ScopeServiceTokens;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Infrastructure.Adapters;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class ConfiguredNyxIdRegistrationTokenAccessorTests
{
    [Fact]
    public async Task GetTokenAsync_ShouldMintScopeServiceCredentialWithoutPersistingSecretState()
    {
        using var keyProvider = new ConfiguredScopeServiceTokenKeyProvider(Options.Create(new ScopeServiceTokenOptions
        {
            Enabled = true,
            Issuer = "https://aevatar.example.com",
            Audience = "aevatar-scope-services",
            SigningKeys =
            [
                new ScopeServiceTokenSigningKeyOptions
                {
                    Kid = "kid-2",
                    Algorithm = ScopeServiceTokenAlgorithms.HmacSha256,
                    Key = "0123456789abcdef0123456789abcdef",
                    Current = true,
                },
            ],
        }));
        var issuer = new ScopeServiceTokenIssuer(
            keyProvider,
            Options.Create(new ScopeServiceTokenOptions
            {
                TokenLifetimeMinutes = 30,
            }));
        using var serviceProvider = new ServiceCollection()
            .AddSingleton<IScopeServiceTokenIssuer>(issuer)
            .BuildServiceProvider();
        var accessor = new ConfiguredNyxIdRegistrationTokenAccessor(
            Options.Create(new NyxIdRegistrationTokenOptions
            {
                OwnerAccessToken = " owner-token ",
            }),
            serviceProvider);

        var result = await accessor.GetTokenAsync(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "default",
            Namespace = "default",
            ServiceId = "orders",
        });

        result.Should().NotBeNull();
        result!.OwnerAccessToken.Should().Be("owner-token");
        result.CredentialKid.Should().Be("kid-2");
        result.ServiceCredential.Should().NotBeNullOrWhiteSpace();

        var principal = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ValidateToken(result.ServiceCredential, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = keyProvider.Issuer,
                ValidateAudience = true,
                ValidAudience = keyProvider.Audience,
                ValidateLifetime = true,
                IssuerSigningKey = keyProvider.CurrentSigningKey.ValidationKey,
                ClockSkew = TimeSpan.FromSeconds(5),
            }, out _);

        principal.Claims.Should().Contain(c => c.Type == "scope_id" && c.Value == "scope-a");
        principal.Claims.Should().Contain(c => c.Type == "aevatar.service_id" && c.Value == "orders");
        principal.Claims.Should().Contain(c => c.Type == "aevatar.service_key" && c.Value == "scope-a:default:default:orders");
    }

    [Fact]
    public async Task GetTokenAsync_WhenOwnerTokenMissing_ShouldReturnNull()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var accessor = new ConfiguredNyxIdRegistrationTokenAccessor(
            Options.Create(new NyxIdRegistrationTokenOptions()),
            serviceProvider);

        var result = await accessor.GetTokenAsync(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "default",
            Namespace = "default",
            ServiceId = "orders",
        });

        result.Should().BeNull();
    }
}
