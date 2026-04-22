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
        var handler = new OidcDocumentHandler(
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
        var handler = new OidcDocumentHandler(
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
        var handler = new OidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, "https://nyx.example.com");
        var token = CreateRelayJwt(key, "https://nyx.example.com", "https://different-audience.example.com", "scope-123", "api-key-123");

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("invalid_audience");
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
        var factory = new StubHttpClientFactory(new HttpClient(handler));
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
        string relayApiKeyId)
    {
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", subject),
                new Claim("relay_api_key_id", relayApiKeyId),
                new Claim("relay", "true"),
            ]),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(5),
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

    private sealed class OidcDocumentHandler : HttpMessageHandler
    {
        private readonly string _discoveryJson;
        private readonly Func<string> _jwksJsonFactory;

        public OidcDocumentHandler(string discoveryJson, Func<string> jwksJsonFactory)
        {
            _discoveryJson = discoveryJson;
            _jwksJsonFactory = jwksJsonFactory;
        }

        public int JwksRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (uri.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_discoveryJson, Encoding.UTF8, "application/json"),
                });
            }

            if (uri.EndsWith("/jwks", StringComparison.Ordinal))
            {
                JwksRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_jwksJsonFactory(), Encoding.UTF8, "application/json"),
                });
            }

            throw new InvalidOperationException($"Unexpected URL: {uri}");
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
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
