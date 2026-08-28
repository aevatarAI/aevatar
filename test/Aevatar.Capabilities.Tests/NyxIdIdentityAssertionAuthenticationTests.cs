using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Aevatar.Authentication.Hosting;
using Aevatar.Capabilities;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgents.Scheduled;
using Aevatar.Mainnet.Host.Api.Responses;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.Capabilities.Tests;

public sealed class NyxIdIdentityAssertionAuthenticationTests
{
    private const string IdentityIssuer = "https://nyxid.example";
    private const string IdentityAudience = "urn:aevatar:api";
    private const string BearerIssuer = "https://bearer.example";
    private const string BearerAudience = "aevatar-bearer";

    [Fact]
    public async Task IdentityHeaderWithoutBearer_ShouldAuthenticateAndBindScopeToSubject()
    {
        using var tokens = new TokenFixture();
        await using var app = await CreateAppAsync(tokens);
        using var request = ScopedRequest(
            "caller-1",
            tokens.CreateIdentityToken("caller-1", assertedScopeId: "victim-scope"));

        using var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("scopeId").GetString().Should().Be("caller-1");
        body.GetProperty("subject").GetString().Should().Be("caller-1");
    }

    [Fact]
    public async Task IdentityHeaderWithDifferentPathScope_ShouldReturnForbidden()
    {
        using var tokens = new TokenFixture();
        await using var app = await CreateAppAsync(tokens);
        using var request = ScopedRequest(
            "victim-scope",
            tokens.CreateIdentityToken("caller-1", assertedScopeId: "victim-scope"));

        using var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task IdentityHeaderWithWrongAudienceOrMissingKid_ShouldReturnUnauthorized()
    {
        using var tokens = new TokenFixture();
        await using var app = await CreateAppAsync(tokens);

        using var wrongAudienceRequest = ScopedRequest(
            "caller-1",
            tokens.CreateIdentityToken("caller-1", audience: "other-service"));
        using var wrongAudienceResponse = await app.GetTestClient().SendAsync(wrongAudienceRequest);

        using var missingKidRequest = ScopedRequest(
            "caller-1",
            tokens.CreateIdentityToken("caller-1", includeKid: false));
        using var missingKidResponse = await app.GetTestClient().SendAsync(missingKidRequest);

        wrongAudienceResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        missingKidResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ReplayedIdentityHeader_ShouldReturnUnauthorizedOnSecondRequest()
    {
        using var tokens = new TokenFixture();
        await using var app = await CreateAppAsync(tokens);
        var token = tokens.CreateIdentityToken("caller-1", jti: "replayed-jti");

        using var firstResponse = await app.GetTestClient().SendAsync(ScopedRequest("caller-1", token));
        using var secondResponse = await app.GetTestClient().SendAsync(ScopedRequest("caller-1", token));

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task IdentityAssertionShouldAuthenticateWhileBearerRemainsAvailableToWorkflow()
    {
        using var tokens = new TokenFixture();
        await using var app = await CreateAppAsync(tokens);
        using var request = ScopedRequest(
            "identity-caller",
            tokens.CreateIdentityToken("identity-caller"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            tokens.CreateBearerToken("bearer-caller"));

        using var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("subject").GetString().Should().Be("identity-caller");
        body.GetProperty("authenticationType").GetString().Should().Be(
            NyxIdIdentityAssertionAuthentication.Scheme);
        body.GetProperty("workflowAuthoritySubject").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("workflowBearerPreserved").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task InvalidIdentityAssertionWithValidBearer_ShouldReturnUnauthorized()
    {
        using var tokens = new TokenFixture();
        await using var app = await CreateAppAsync(tokens);
        using var request = ScopedRequest("bearer-caller", "not-a-jwt");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            tokens.CreateBearerToken("bearer-caller"));

        using var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResponsesResolver_ShouldReuseAuthenticationResultWithoutConsumingJtiTwice()
    {
        using var tokens = new TokenFixture();
        await using var app = await CreateAppAsync(tokens);
        var token = tokens.CreateIdentityToken("caller-1", jti: "responses-jti");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/responses-probe");
        request.Headers.TryAddWithoutValidation(
            NyxIdIdentityAssertionAuthentication.HeaderName,
            token);

        using var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("scopeId").GetString().Should().Be("caller-1");
    }

    private static async Task<WebApplication> CreateAppAsync(TokenFixture tokens)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration["Aevatar:Authentication:Enabled"] = "true";
        builder.Configuration["Aevatar:Authentication:Authority"] = BearerIssuer;
        builder.Configuration["Aevatar:Authentication:Audience"] = BearerAudience;

        builder.AddAevatarAuthentication();
        builder.Services.Configure<ResponsesNyxIdIdentityAssertionOptions>(options =>
        {
            options.Issuer = IdentityIssuer;
            options.ExpectedAudience = IdentityAudience;
            options.JwksUri = "https://nyxid.example/.well-known/jwks.json";
            options.ClockSkewSeconds = 0;
            options.MaximumLifetimeSeconds = 60;
        });
        builder.Services.AddSingleton<IHttpClientFactory>(tokens);
        builder.Services.AddSingleton<IIdentityAssertionReplayGuard>(
            _ => new InMemoryIdentityAssertionReplayGuard(TimeProvider.System));
        builder.Services.AddSingleton<NyxIdIdentityAssertionValidator>();
        builder.Services.AddSingleton<INyxIdCurrentUserResolver, FailingCurrentUserResolver>();
        builder.Services.AddSingleton<NyxIdResponsesCallerScopeResolver>();
        builder.AddNyxIdIdentityAssertionAuthentication();
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = BearerIssuer };
            configuration.SigningKeys.Add(tokens.ValidationKey);
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/api/scopes/{scopeId}/probe", HandleScopedProbe).RequireAuthorization();
        app.MapGet("/responses-probe", async (
            HttpContext http,
            NyxIdResponsesCallerScopeResolver resolver,
            CancellationToken ct) =>
        {
            var identityToken = http.Request.Headers[NyxIdIdentityAssertionAuthentication.HeaderName]
                .FirstOrDefault();
            var scope = await resolver.ResolveAsync(
                new ResponsesCallerScopeResolutionContext(string.Empty, identityToken, null),
                ct);
            return Results.Json(new { scopeId = scope.ScopeId });
        }).RequireAuthorization();
        await app.StartAsync();
        return app;
    }

    private static IResult HandleScopedProbe(HttpContext http, string scopeId)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var callerCredential = WorkflowCallerCredentialExtractor.Extract(http).Credential;

        return Results.Json(new
        {
            scopeId,
            subject = http.User.FindFirstValue(JwtRegisteredClaimNames.Sub),
            authenticationType = http.User.Identity?.AuthenticationType,
            workflowAuthoritySubject = callerCredential?.NyxIdAuthority?.ExternalUserId,
            workflowBearerPreserved = !string.IsNullOrWhiteSpace(callerCredential?.BearerToken),
        });
    }

    private static HttpRequestMessage ScopedRequest(string scopeId, string identityToken)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/scopes/{Uri.EscapeDataString(scopeId)}/probe");
        request.Headers.TryAddWithoutValidation(
            NyxIdIdentityAssertionAuthentication.HeaderName,
            identityToken);
        return request;
    }

    private sealed class TokenFixture : IHttpClientFactory, IDisposable
    {
        private const string KeyId = "nyxid-key";
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly RsaSecurityKey _signingKey;
        private readonly string _jwks;

        public TokenFixture()
        {
            _signingKey = new RsaSecurityKey(_rsa) { KeyId = KeyId };
            _jwks = JsonSerializer.Serialize(new
            {
                keys = new[] { JsonWebKeyConverter.ConvertFromSecurityKey(_signingKey) },
            });
        }

        public SecurityKey ValidationKey => _signingKey;

        public string CreateIdentityToken(
            string subject,
            string audience = IdentityAudience,
            string? jti = null,
            string? assertedScopeId = null,
            bool includeKid = true)
        {
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, subject),
                new(JwtRegisteredClaimNames.Jti, jti ?? Guid.NewGuid().ToString("N")),
            };
            if (!string.IsNullOrWhiteSpace(assertedScopeId))
                claims.Add(new Claim("scope_id", assertedScopeId));

            return CreateToken(
                IdentityIssuer,
                audience,
                claims,
                lifetime: TimeSpan.FromSeconds(60),
                includeKid);
        }

        public string CreateBearerToken(string subject) => CreateToken(
            BearerIssuer,
            BearerAudience,
            [
                new Claim(JwtRegisteredClaimNames.Sub, subject),
                new Claim("scope_id", subject),
            ],
            lifetime: TimeSpan.FromMinutes(5),
            includeKid: true);

        private string CreateToken(
            string issuer,
            string audience,
            IReadOnlyCollection<Claim> claims,
            TimeSpan lifetime,
            bool includeKid)
        {
            var now = DateTime.UtcNow;
            var signingKey = includeKid
                ? _signingKey
                : new RsaSecurityKey(_rsa);
            var allClaims = claims.Concat([
                new Claim(
                    JwtRegisteredClaimNames.Iat,
                    new DateTimeOffset(now).ToUnixTimeSeconds().ToString(),
                    ClaimValueTypes.Integer64),
            ]);
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Subject = new ClaimsIdentity(allClaims),
                NotBefore = now.AddSeconds(-1),
                Expires = now.Add(lifetime),
                SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
            };
            return new JwtSecurityTokenHandler
            {
                SetDefaultTimesOnTokenCreation = false,
            }.CreateEncodedJwt(descriptor);
        }

        public HttpClient CreateClient(string name) => new(new StaticJwksHandler(_jwks));

        public void Dispose() => _rsa.Dispose();
    }

    private sealed class StaticJwksHandler(string jwks) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jwks),
            });
        }
    }

    private sealed class FailingCurrentUserResolver : INyxIdCurrentUserResolver
    {
        public Task<string?> ResolveCurrentUserIdAsync(
            string nyxIdAccessToken,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Identity assertions must not use bearer lookup.");
    }
}
