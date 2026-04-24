using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.NyxIdRelay;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.AI.Tests;

public sealed class NyxIdRelayAuthValidatorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public async Task ValidateAsync_ShouldAcceptValidRelayCallback()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, "https://nyx.example.com");
        var token = CreateRelayJwt(key, "https://nyx.example.com", "https://nyx.example.com", "scope-123", "api-key-123");
        var request = CreateRelayRequest(token, "api-key-123");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.ScopeId.Should().Be("scope-123");
        result.RelayApiKeyId.Should().Be("api-key-123");
        result.UserAccessToken.Should().Be(token);
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
        var request = CreateRelayRequest(token, "api-key-123");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

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
        var request = CreateRelayRequest(token, "api-key-123");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("SecurityTokenInvalidAudienceException");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectMissingUserToken()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, "https://nyx.example.com");
        var request = CreateRelayRequest(userToken: null, agentApiKeyId: "api-key-123");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("missing_user_token");
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
        var request = CreateRelayRequest(token, "api-key-123");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("SecurityTokenExpiredException");
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
        var request = CreateRelayRequest(token, "api-key-123");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("SecurityTokenInvalidIssuerException");
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
        var request = CreateRelayRequest(token, "api-key-123");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().BeOneOf(
            nameof(SecurityTokenInvalidAlgorithmException),
            nameof(SecurityTokenInvalidSignatureException));
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
        var request = CreateRelayRequest(token, "api-key-123");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("jwt_missing_sub");
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
        var request = CreateRelayRequest(token, "api-key-123");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("jwt_missing_relay_api_key_id");
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
        var request = CreateRelayRequest("not-a-jwt", "api-key-123");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(SecurityTokenMalformedException));
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectInvalidSignature()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, "https://nyx.example.com");
        var token = CreateRelayJwt(key, "https://nyx.example.com", "https://nyx.example.com", "scope-123", "api-key-123");
        var request = CreateRelayRequest(token, "api-key-123", overrideSignature: "bad-signature");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("invalid_signature");
    }

    [Fact]
    public async Task ValidateAsync_ShouldLogSignatureDiagnostics_WhenRegistrationCredentialSignatureFails()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var logger = new CaptureLogger<NyxIdRelayAuthValidator>();
        var validator = CreateValidator(handler, "https://nyx.example.com", logger: logger);
        var token = CreateRelayJwt(key, "https://nyx.example.com", "https://nyx.example.com", "scope-123", "api-key-123");
        var request = CreateRelayRequest(token, "api-key-123", overrideSignature: "bad-signature");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("invalid_signature");
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning &&
            entry.Message.Contains("Nyx relay signature verification failed", StringComparison.Ordinal) &&
            entry.Message.Contains("relay_api_key_id=api-key-123", StringComparison.Ordinal) &&
            entry.Message.Contains("payload_agent_api_key_id=api-key-123", StringComparison.Ordinal) &&
            entry.Message.Contains("message_id=msg-1", StringComparison.Ordinal) &&
            entry.Message.Contains("registration_id=reg-1", StringComparison.Ordinal) &&
            entry.Message.Contains("credential_resolved=True", StringComparison.Ordinal) &&
            entry.Message.Contains("signing_secret_source=registration_credential", StringComparison.Ordinal) &&
            entry.Message.Contains("signature_header_present=True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateAsync_ShouldLogSignatureDiagnostics_WhenFallingBackToGlobalHmacSecret()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var logger = new CaptureLogger<NyxIdRelayAuthValidator>();
        var validator = CreateValidator(
            handler,
            "https://nyx.example.com",
            logger: logger,
            registrationCredentialResolver: new NullRegistrationCredentialResolver(),
            globalHmacSecret: "fallback-secret");
        var token = CreateRelayJwt(key, "https://nyx.example.com", "https://nyx.example.com", "scope-123", "api-key-123");
        var request = CreateRelayRequest(token, "api-key-123", overrideSignature: "bad-signature");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("invalid_signature");
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning &&
            entry.Message.Contains("Nyx relay signature verification failed", StringComparison.Ordinal) &&
            entry.Message.Contains("credential_resolved=False", StringComparison.Ordinal) &&
            entry.Message.Contains("signing_secret_source=global_hmac_secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectMessageIdMismatch()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, "https://nyx.example.com");
        var token = CreateRelayJwt(key, "https://nyx.example.com", "https://nyx.example.com", "scope-123", "api-key-123");
        var request = CreateRelayRequest(token, "api-key-123", headerMessageId: "msg-other");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("message_id_mismatch");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectRelayApiKeyMismatch()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, "https://nyx.example.com");
        var token = CreateRelayJwt(key, "https://nyx.example.com", "https://nyx.example.com", "scope-123", "api-key-123");
        var request = CreateRelayRequest(token, "api-key-other");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("relay_api_key_mismatch");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectMissingTimestampHeader()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, "https://nyx.example.com");
        var token = CreateRelayJwt(key, "https://nyx.example.com", "https://nyx.example.com", "scope-123", "api-key-123");
        var request = CreateRelayRequest(token, "api-key-123");
        request.HttpContext.Request.Headers.Remove("X-NyxID-Timestamp");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("missing_timestamp_header");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectReplayWithinWindow()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson("https://nyx.example.com", "https://nyx.example.com/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, "https://nyx.example.com");
        var token = CreateRelayJwt(key, "https://nyx.example.com", "https://nyx.example.com", "scope-123", "api-key-123");
        var firstRequest = CreateRelayRequest(token, "api-key-123", messageId: "msg-replay");
        var secondRequest = CreateRelayRequest(token, "api-key-123", messageId: "msg-replay");

        var first = await validator.ValidateAsync(
            firstRequest.HttpContext,
            firstRequest.BodyBytes,
            firstRequest.Payload,
            CancellationToken.None);
        var second = await validator.ValidateAsync(
            secondRequest.HttpContext,
            secondRequest.BodyBytes,
            secondRequest.Payload,
            CancellationToken.None);

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeFalse();
        second.ErrorCode.Should().Be("replay_detected");
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

    private static NyxIdRelayAuthValidator CreateValidator(
        HttpMessageHandler handler,
        string baseUrl,
        string hmacSecret = "relay-secret",
        ILogger<NyxIdRelayAuthValidator>? logger = null,
        INyxIdRelayRegistrationCredentialResolver? registrationCredentialResolver = null,
        string? globalHmacSecret = null)
    {
        var factory = new NyxRelayTestHttpClientFactory(new HttpClient(handler));
        return new NyxIdRelayAuthValidator(
            factory,
            new NyxIdToolOptions { BaseUrl = baseUrl },
            new NyxIdRelayOptions
            {
                OidcCacheTtlSeconds = 60,
                JwtClockSkewSeconds = 0,
                RequireMessageIdHeader = true,
                RequireTimestampHeader = true,
                HmacSecret = globalHmacSecret,
            },
            logger ?? NullLogger<NyxIdRelayAuthValidator>.Instance,
            registrationCredentialResolver ?? new StaticRegistrationCredentialResolver(hmacSecret),
            new NyxIdRelayReplayGuard());
    }

    private static RelayRequest CreateRelayRequest(
        string? userToken,
        string agentApiKeyId,
        string messageId = "msg-1",
        string? headerMessageId = null,
        string? overrideSignature = null)
    {
        var payload = new NyxIdRelayCallbackPayload
        {
            MessageId = messageId,
            Platform = "lark",
            Agent = new NyxIdRelayAgentPayload
            {
                ApiKeyId = agentApiKeyId,
            },
            Conversation = new NyxIdRelayConversationPayload
            {
                Id = "conv-1",
                Type = "group",
            },
            Sender = new NyxIdRelaySenderPayload
            {
                PlatformId = "user-1",
                DisplayName = "User One",
            },
            Content = new NyxIdRelayContentPayload
            {
                Type = "text",
                Text = "hello",
            },
            Timestamp = "2026-04-23T12:00:00Z",
        };

        var body = JsonSerializer.Serialize(payload, JsonOptions);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var http = new DefaultHttpContext();
        if (!string.IsNullOrWhiteSpace(userToken))
            http.Request.Headers["X-NyxID-User-Token"] = userToken;
        http.Request.Headers["X-NyxID-Message-Id"] = headerMessageId ?? messageId;
        http.Request.Headers["X-NyxID-Signature"] = overrideSignature ?? ComputeRelaySignature("relay-secret", bodyBytes);
        http.Request.Headers["X-NyxID-Timestamp"] = DateTimeOffset.UtcNow.ToString("O");

        return new RelayRequest(http, bodyBytes, payload);
    }

    private static string ComputeRelaySignature(string secret, byte[] bodyBytes)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(bodyBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
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

    private sealed record RelayRequest(
        DefaultHttpContext HttpContext,
        byte[] BodyBytes,
        NyxIdRelayCallbackPayload Payload);

    private sealed class StaticRegistrationCredentialResolver : INyxIdRelayRegistrationCredentialResolver
    {
        private readonly string _apiKeyHash;

        public StaticRegistrationCredentialResolver(string apiKeyHash)
        {
            _apiKeyHash = apiKeyHash;
        }

        public Task<NyxIdRelayRegistrationCredential?> ResolveAsync(string relayApiKeyId, CancellationToken ct = default) =>
            Task.FromResult<NyxIdRelayRegistrationCredential?>(
                new NyxIdRelayRegistrationCredential("reg-1", relayApiKeyId, _apiKeyHash));
    }

    private sealed class NullRegistrationCredentialResolver : INyxIdRelayRegistrationCredentialResolver
    {
        public Task<NyxIdRelayRegistrationCredential?> ResolveAsync(string relayApiKeyId, CancellationToken ct = default) =>
            Task.FromResult<NyxIdRelayRegistrationCredential?>(null);
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<CapturedLogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new CapturedLogEntry(logLevel, formatter(state, exception)));
        }

        public sealed record CapturedLogEntry(LogLevel Level, string Message);

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
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
