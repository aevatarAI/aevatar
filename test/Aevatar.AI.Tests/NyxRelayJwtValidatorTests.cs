using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.AI.Tests;

public sealed class NyxRelayJwtValidatorTests
{
    [Fact]
    public async Task ValidateAsync_ShouldAcceptValidRelayJwt()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, "https://nyx.example.com");
        var token = CreateRelayJwt(key, "https://nyx.example.com", "https://nyx.example.com", "scope-123", "api-key-123");

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Subject.Should().Be("scope-123");
        result.RelayApiKeyId.Should().Be("api-key-123");
        result.Principal.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateAsync_ShouldRefreshJwks_WhenSigningKeyWasRotated()
    {
        using var staleRsa = RSA.Create(2048);
        using var freshRsa = RSA.Create(2048);
        var staleKey = CreateSigningKey(staleRsa, "kid-stale");
        var freshKey = CreateSigningKey(freshRsa, "kid-fresh");
        var callCount = 0;
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () =>
            {
                callCount++;
                return callCount == 1 ? CreateJwksJson(staleKey) : CreateJwksJson(freshKey);
            });
        var validator = CreateValidator(handler, "https://nyx.example.com");
        var token = CreateRelayJwt(freshKey, "https://nyx.example.com", "https://nyx.example.com", "scope-123", "api-key-123");

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        handler.JwksRequests.Should().Be(2);
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectWrongAudience()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, "https://nyx.example.com");
        var token = CreateRelayJwt(key, "https://nyx.example.com", "https://different-audience.example.com", "scope-123", "api-key-123");

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("invalid_audience");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectMissingToken()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, "https://nyx.example.com");

        var result = await validator.ValidateAsync("   ", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("missing_token");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectExpiredToken()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, "https://nyx.example.com");
        var token = CreateRelayJwt(
            key,
            "https://nyx.example.com",
            "https://nyx.example.com",
            "scope-123",
            "api-key-123",
            notBeforeUtc: DateTime.UtcNow.AddMinutes(-10),
            expiresAtUtc: DateTime.UtcNow.AddMinutes(-5));

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("token_expired");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectWrongIssuer()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, "https://nyx.example.com");
        var token = CreateRelayJwt(
            key,
            "https://issuer.other.example.com",
            "https://nyx.example.com",
            "scope-123",
            "api-key-123");

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("invalid_issuer");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectUnexpectedSigningAlgorithm()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, "https://nyx.example.com");
        var token = CreateRelayJwt(
            key,
            "https://nyx.example.com",
            "https://nyx.example.com",
            "scope-123",
            "api-key-123",
            signingAlgorithm: SecurityAlgorithms.RsaSha512);

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(nameof(SecurityTokenInvalidSignatureException));
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectTokenWithoutSubject()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, "https://nyx.example.com");
        var token = CreateRelayJwt(
            key,
            "https://nyx.example.com",
            "https://nyx.example.com",
            subject: "scope-123",
            relayApiKeyId: "api-key-123",
            includeSubject: false);

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("jwt_missing_sub");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectTokenWithoutRelayApiKeyId()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, "https://nyx.example.com");
        var token = CreateRelayJwt(
            key,
            "https://nyx.example.com",
            "https://nyx.example.com",
            subject: "scope-123",
            relayApiKeyId: "api-key-123",
            includeRelayApiKeyId: false);

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("jwt_missing_relay_api_key_id");
    }

    [Fact]
    public async Task ValidateAsync_ShouldReturnSecurityTokenError_ForMalformedToken()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, "https://nyx.example.com");

        var result = await validator.ValidateAsync("not-a-jwt", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(nameof(SecurityTokenMalformedException));
    }

    [Fact]
    public async Task SendChannelRelayTextReplyAsync_ShouldPostExpectedBody()
    {
        var handler = new CaptureHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"message_id":"reply-1","platform_message_id":"platform-1"}""", Encoding.UTF8, "application/json"),
            });
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);

        var result = await client.SendChannelRelayTextReplyAsync("relay-token", "message-123", "hello", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.MessageId.Should().Be("reply-1");
        result.PlatformMessageId.Should().Be("platform-1");
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().Be("https://nyx.example.com/api/v1/channel-relay/reply");
        handler.LastRequest.Headers.Authorization.Should().BeEquivalentTo(new AuthenticationHeaderValue("Bearer", "relay-token"));

        handler.LastRequestBody.Should().NotBeNull();
        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        document.RootElement.GetProperty("message_id").GetString().Should().Be("message-123");
        document.RootElement.GetProperty("reply").GetProperty("text").GetString().Should().Be("hello");
    }

    private static NyxRelayJwtValidator CreateValidator(HttpMessageHandler handler, string baseUrl)
    {
        var factory = new NyxRelayTestHttpClientFactory(new HttpClient(handler));
        return new NyxRelayJwtValidator(
            factory,
            new NyxIdToolOptions { BaseUrl = baseUrl },
            new NyxIdRelayOptions
            {
                OidcCacheTtlSeconds = 60,
                JwtClockSkewSeconds = 0,
            },
            NullLogger<NyxRelayJwtValidator>.Instance);
    }

    private static RsaSecurityKey CreateSigningKey(RSA rsa, string keyId) =>
        new(rsa)
        {
            KeyId = keyId,
        };

    private static string CreateRelayJwt(
        RsaSecurityKey key,
        string issuer,
        string audience,
        string subject,
        string relayApiKeyId,
        bool includeSubject = true,
        bool includeRelayApiKeyId = true,
        DateTime? notBeforeUtc = null,
        DateTime? expiresAtUtc = null,
        string signingAlgorithm = SecurityAlgorithms.RsaSha256)
    {
        var credentials = new SigningCredentials(key, signingAlgorithm);
        var claims = new List<Claim>
        {
            new("relay", "true"),
        };
        if (includeSubject)
            claims.Add(new Claim("sub", subject));
        if (includeRelayApiKeyId)
            claims.Add(new Claim("relay_api_key_id", relayApiKeyId));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = notBeforeUtc ?? DateTime.UtcNow.AddMinutes(-1),
            Expires = expiresAtUtc ?? DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = credentials,
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.CreateEncodedJwt(descriptor);
    }

    private static string CreateDiscoveryJson(string issuer, string jwksUri) =>
        $$"""
          {
            "issuer": "{{issuer}}",
            "jwks_uri": "{{jwksUri}}"
          }
          """;

    private static string CreateJwksJson(SecurityKey key)
    {
        var jsonWebKey = JsonWebKeyConverter.ConvertFromSecurityKey(key);
        return JsonSerializer.Serialize(new
        {
            keys = new[] { jsonWebKey },
        });
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public CaptureHandler(HttpResponseMessage response) => _response = response;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _response;
        }
    }
}
