using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using Aevatar.Authentication.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.Bootstrap.Tests;

public sealed class AuthenticationHttpPipelineTests
{
    private const string Authority = "https://nyxid.example.com";
    private const string DiscoveredIssuer = "https://nyx-api.example.com";
    private const string Audience = "aevatar-api";
    private const string ScopeIssuer = "https://scope.example.com";
    private const string ScopeAudience = "aevatar-scope-services";
    private const string ScopeKeyId = "scope-key";
    private const string ScopeSigningKey = "0123456789abcdef0123456789abcdef";
    private const string ResourceUri = "https://api.example.com/resource";

    [Fact]
    public async Task DPoPAuthorization_WithScopeTokensEnabled_ShouldUseDiscoveryKeyAndValidateAth()
    {
        using var authorityKey = RSA.Create(2048);
        using var proofKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var discovery = new DiscoveryDocumentHandler(authorityKey);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        ConfigureAuthentication(builder, dpopEnabled: true, scopeTokensEnabled: true);
        builder.AddAevatarAuthentication();
        builder.Services.Replace(ServiceDescriptor.Singleton<IDPoPReplayGuard, AcceptingReplayGuard>());
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
        {
            var retriever = new HttpDocumentRetriever(new HttpClient(discovery))
            {
                RequireHttps = true,
            };
            jwt.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{Authority}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                retriever);
        });

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/resource", () => "ok").RequireAuthorization();
        await app.StartAsync();

        var jkt = ComputeEcThumbprint(proofKey);
        var accessToken = CreateAccessToken(authorityKey, jkt);
        var proof = CreateProof(proofKey, accessToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, ResourceUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", accessToken);
        request.Headers.Add("DPoP", proof);

        using var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        discovery.MetadataRequests.Should().Be(1);
        discovery.JwksRequests.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_WhenDPoPEnabledWithNoOpReplayGuard_ShouldFailClosed()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        ConfigureAuthentication(builder, dpopEnabled: true, scopeTokensEnabled: false);
        builder.AddAevatarAuthentication();
        await using var app = builder.Build();

        var act = () => app.StartAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*shared IDPoPReplayGuard*");
    }

    [Theory]
    [InlineData(false, false, Audience, HttpStatusCode.OK)]
    [InlineData(false, false, "another-api", HttpStatusCode.Unauthorized)]
    [InlineData(true, false, Audience, HttpStatusCode.OK)]
    [InlineData(true, false, ScopeAudience, HttpStatusCode.Unauthorized)]
    [InlineData(true, true, ScopeAudience, HttpStatusCode.OK)]
    [InlineData(true, true, Audience, HttpStatusCode.Unauthorized)]
    public async Task BearerAuthorization_ShouldBindAudienceToTokenIssuer(
        bool scopeTokensEnabled,
        bool issueScopeToken,
        string tokenAudience,
        HttpStatusCode expectedStatusCode)
    {
        using var authorityKey = RSA.Create(2048);
        var discovery = new DiscoveryDocumentHandler(authorityKey);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseTestServer();
        ConfigureAuthentication(builder, dpopEnabled: false, scopeTokensEnabled);
        builder.AddAevatarAuthentication();
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
        {
            var retriever = new HttpDocumentRetriever(new HttpClient(discovery))
            {
                RequireHttps = true,
            };
            jwt.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{Authority}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                retriever);
        });

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/resource", () => "ok").RequireAuthorization();
        await app.StartAsync();

        var accessToken = issueScopeToken
            ? CreateScopeAccessToken(tokenAudience)
            : CreateAccessToken(
                authorityKey,
                confirmationThumbprint: null,
                audience: tokenAudience);
        using var request = new HttpRequestMessage(HttpMethod.Get, ResourceUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(expectedStatusCode);
    }

    private static void ConfigureAuthentication(
        WebApplicationBuilder builder,
        bool dpopEnabled,
        bool scopeTokensEnabled)
    {
        builder.Configuration["Aevatar:Authentication:Enabled"] = "true";
        builder.Configuration["Aevatar:Authentication:Authority"] = Authority;
        builder.Configuration["Aevatar:Authentication:Audience"] = Audience;
        builder.Configuration["Aevatar:Authentication:DPoP:Enabled"] = dpopEnabled.ToString();
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:Enabled"] =
            scopeTokensEnabled.ToString();
        if (!scopeTokensEnabled)
            return;

        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:Issuer"] =
            ScopeIssuer;
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:Audience"] =
            ScopeAudience;
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:SigningKeys:0:Kid"] =
            ScopeKeyId;
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:SigningKeys:0:Algorithm"] =
            "HS256";
        builder.Configuration["Aevatar:Authentication:ScopeServiceTokens:SigningKeys:0:Key"] =
            ScopeSigningKey;
    }

    private static string CreateAccessToken(
        RSA authorityKey,
        string? confirmationThumbprint,
        string audience = Audience)
    {
        var header = new JwtHeader(new SigningCredentials(
            new RsaSecurityKey(authorityKey) { KeyId = DiscoveryDocumentHandler.KeyId },
            SecurityAlgorithms.RsaSha256));
        var now = DateTimeOffset.UtcNow;
        var payload = new JwtPayload
        {
            [JwtRegisteredClaimNames.Iss] = DiscoveredIssuer,
            [JwtRegisteredClaimNames.Aud] = audience,
            [JwtRegisteredClaimNames.Sub] = "user-alpha",
            [JwtRegisteredClaimNames.Iat] = now.ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Exp] = now.AddMinutes(5).ToUnixTimeSeconds(),
        };
        if (!string.IsNullOrWhiteSpace(confirmationThumbprint))
            payload["cnf"] = new Dictionary<string, object> { ["jkt"] = confirmationThumbprint };

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    private static string CreateScopeAccessToken(string audience)
    {
        var header = new JwtHeader(new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ScopeSigningKey)) { KeyId = ScopeKeyId },
            SecurityAlgorithms.HmacSha256));
        var now = DateTimeOffset.UtcNow;
        var payload = new JwtPayload
        {
            [JwtRegisteredClaimNames.Iss] = ScopeIssuer,
            [JwtRegisteredClaimNames.Aud] = audience,
            [JwtRegisteredClaimNames.Sub] = "scope-alpha",
            [JwtRegisteredClaimNames.Iat] = now.ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Exp] = now.AddMinutes(5).ToUnixTimeSeconds(),
        };

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    private static string CreateProof(ECDsa key, string accessToken)
    {
        var publicJwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(key));
        var header =
            $"{{\"typ\":\"dpop+jwt\",\"alg\":\"ES256\",\"jwk\":{{\"kty\":\"EC\",\"crv\":\"{publicJwk.Crv}\",\"x\":\"{publicJwk.X}\",\"y\":\"{publicJwk.Y}\"}}}}";
        var ath = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));
        var payload =
            $"{{\"htm\":\"GET\",\"htu\":\"{ResourceUri}\",\"iat\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()},\"jti\":\"proof-alpha\",\"ath\":\"{ath}\"}}";
        var signingInput =
            $"{Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(header))}." +
            Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(payload));
        var signature = key.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return $"{signingInput}.{Base64UrlEncoder.Encode(signature)}";
    }

    private static string ComputeEcThumbprint(ECDsa key)
    {
        var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(key));
        var canonical = $"{{\"crv\":\"{jwk.Crv}\",\"kty\":\"EC\",\"x\":\"{jwk.X}\",\"y\":\"{jwk.Y}\"}}";
        return Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed class AcceptingReplayGuard : IDPoPReplayGuard
    {
        public ValueTask<bool> TryRegisterAsync(
            string jti,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class DiscoveryDocumentHandler : HttpMessageHandler
    {
        internal const string KeyId = "authority-key";
        private readonly string _jwks;

        public DiscoveryDocumentHandler(RSA authorityKey)
        {
            var parameters = authorityKey.ExportParameters(includePrivateParameters: false);
            var modulus = Base64UrlEncoder.Encode(parameters.Modulus!);
            var exponent = Base64UrlEncoder.Encode(parameters.Exponent!);
            _jwks =
                $"{{\"keys\":[{{\"kty\":\"RSA\",\"use\":\"sig\",\"kid\":\"{KeyId}\",\"alg\":\"RS256\",\"n\":\"{modulus}\",\"e\":\"{exponent}\"}}]}}";
        }

        public int MetadataRequests { get; private set; }
        public int JwksRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (path == "/.well-known/openid-configuration")
            {
                MetadataRequests++;
                return Task.FromResult(JsonResponse(
                    $"{{\"issuer\":\"{DiscoveredIssuer}\",\"jwks_uri\":\"{DiscoveredIssuer}/.well-known/jwks.json\"}}"));
            }

            if (path == "/.well-known/jwks.json")
            {
                JwksRequests++;
                return Task.FromResult(JsonResponse(_jwks));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
