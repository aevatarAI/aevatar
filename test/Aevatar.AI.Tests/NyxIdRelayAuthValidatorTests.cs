using System.IdentityModel.Tokens.Jwt;
using System.Diagnostics.Metrics;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.AI.Tests;

public sealed class NyxIdRelayAuthValidatorTests
{
    private const string Issuer = "https://nyx.example.com";
    private const string CallbackAudience = "channel-relay/callback";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public async Task ValidateAsync_ShouldAcceptValidCallbackJwt()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, Issuer);
        var request = CreateRelayRequest(key, userToken: "user-token-1");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.ScopeId.Should().Be("scope-123");
        result.RelayApiKeyId.Should().Be("api-key-123");
        result.UserAccessToken.Should().Be("user-token-1");
        result.Principal.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateAsync_ShouldAcceptCallback_WhenUserTokenIsMissing()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"),
            () => CreateJwksJson(key));
        var validator = CreateValidator(handler, Issuer);
        var request = CreateRelayRequest(key, userToken: null);

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.UserAccessToken.Should().BeNull();
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectMissingCallbackToken()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var validator = CreateValidator(
            new NyxRelayOidcDocumentHandler(CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"), () => CreateJwksJson(key)),
            Issuer);
        var request = CreateRelayRequest(key, includeCallbackToken: false);

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("callback_jwt_missing");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRecordFailureMetricWithReason()
    {
        using var metrics = new RelayMetricCapture();
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var validator = CreateValidator(
            new NyxRelayOidcDocumentHandler(CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"), () => CreateJwksJson(key)),
            Issuer);
        var request = CreateRelayRequest(key, includeCallbackToken: false);

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        metrics.Failures.Should().Contain(measurement =>
            measurement.Value == 1 &&
            measurement.Tags.Any(tag =>
                tag.Key == "reason" &&
                string.Equals(tag.Value as string, "callback_jwt_missing", StringComparison.Ordinal)));
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
            CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"),
            () =>
            {
                callCount++;
                return callCount == 1 ? CreateJwksJson(staleKey) : CreateJwksJson(freshKey);
            });
        var validator = CreateValidator(handler, Issuer);
        var request = CreateRelayRequest(freshKey);

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        handler.JwksRequests.Should().Be(2);
    }

    [Fact]
    public async Task ValidateAsync_ShouldThrottleJwksRefresh_WhenKidMissRepeats()
    {
        using var knownRsa = RSA.Create(2048);
        using var unknownRsa1 = RSA.Create(2048);
        using var unknownRsa2 = RSA.Create(2048);
        var knownKey = CreateSigningKey(knownRsa, "kid-known");
        var unknownKey1 = CreateSigningKey(unknownRsa1, "kid-unknown-1");
        var unknownKey2 = CreateSigningKey(unknownRsa2, "kid-unknown-2");
        var handler = new NyxRelayOidcDocumentHandler(
            CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"),
            () => CreateJwksJson(knownKey));
        var validator = CreateValidator(handler, Issuer, kidMissRefreshCooldownSeconds: 10);
        var firstRequest = CreateRelayRequest(unknownKey1, correlationId: "corr-kid-1");
        var secondRequest = CreateRelayRequest(unknownKey2, messageId: "msg-kid-2", correlationId: "corr-kid-2");

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

        first.Succeeded.Should().BeFalse();
        second.Succeeded.Should().BeFalse();
        first.ErrorCode.Should().Be("callback_jwt_kid_not_found");
        second.ErrorCode.Should().Be("callback_jwt_kid_not_found");
        handler.JwksRequests.Should().Be(2);
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectWrongAudience()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var validator = CreateValidator(
            new NyxRelayOidcDocumentHandler(CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"), () => CreateJwksJson(key)),
            Issuer);
        var request = CreateRelayRequest(key, audience: "https://wrong.example.com");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("callback_jwt_audience_mismatch");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectExpiredToken()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var validator = CreateValidator(
            new NyxRelayOidcDocumentHandler(CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"), () => CreateJwksJson(key)),
            Issuer);
        var request = CreateRelayRequest(
            key,
            notBeforeUtc: DateTime.UtcNow.AddMinutes(-10),
            expiresAtUtc: DateTime.UtcNow.AddMinutes(-2));

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("callback_jwt_lifetime_invalid");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectWrongIssuer()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var validator = CreateValidator(
            new NyxRelayOidcDocumentHandler(CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"), () => CreateJwksJson(key)),
            Issuer);
        var request = CreateRelayRequest(key, issuer: "https://issuer.other.example.com");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("callback_jwt_issuer_mismatch");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectUnexpectedSigningAlgorithm()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var validator = CreateValidator(
            new NyxRelayOidcDocumentHandler(CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"), () => CreateJwksJson(key)),
            Issuer);
        var request = CreateRelayRequest(key, signingAlgorithm: SecurityAlgorithms.RsaSha512);

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("callback_jwt_signature_invalid");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectMalformedToken()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var validator = CreateValidator(
            new NyxRelayOidcDocumentHandler(CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"), () => CreateJwksJson(key)),
            Issuer);
        var request = CreateRelayRequest(key, callbackTokenOverride: "not-a-jwt");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().BeOneOf("callback_jwt_malformed", "callback_jwt_invalid");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectInvalidSignature()
    {
        using var rsa = RSA.Create(2048);
        using var otherRsa = RSA.Create(2048);
        var jwksKey = CreateSigningKey(rsa, "kid-1");
        var signingKey = CreateSigningKey(otherRsa, "kid-1");
        var validator = CreateValidator(
            new NyxRelayOidcDocumentHandler(CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"), () => CreateJwksJson(jwksKey)),
            Issuer);
        var request = CreateRelayRequest(signingKey);

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("callback_jwt_signature_invalid");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectMessageIdMismatch()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var validator = CreateValidator(
            new NyxRelayOidcDocumentHandler(CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"), () => CreateJwksJson(key)),
            Issuer);
        var request = CreateRelayRequest(key, tokenMessageId: "msg-other");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("callback_jwt_message_id_mismatch");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectRelayApiKeyMismatch()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var validator = CreateValidator(
            new NyxRelayOidcDocumentHandler(CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"), () => CreateJwksJson(key)),
            Issuer);
        var request = CreateRelayRequest(key, payloadAgentApiKeyId: "api-key-other");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("callback_jwt_api_key_id_mismatch");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectPlatformMismatch()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var validator = CreateValidator(
            new NyxRelayOidcDocumentHandler(CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"), () => CreateJwksJson(key)),
            Issuer);
        var request = CreateRelayRequest(key, tokenPlatform: "slack");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("callback_jwt_platform_mismatch");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectCorrelationMismatch()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var validator = CreateValidator(
            new NyxRelayOidcDocumentHandler(CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"), () => CreateJwksJson(key)),
            Issuer);
        var request = CreateRelayRequest(key, tokenJti: "different-jti");

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("callback_jwt_correlation_id_mismatch");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectRawBodyHashMismatch()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var validator = CreateValidator(
            new NyxRelayOidcDocumentHandler(CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"), () => CreateJwksJson(key)),
            Issuer);
        var request = CreateRelayRequest(key);
        request = request with { BodyBytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(request.BodyBytes) + "\n") };

        var result = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("callback_jwt_body_hash_mismatch");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRejectReplayWithinWindow()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa, "kid-1");
        var validator = CreateValidator(
            new NyxRelayOidcDocumentHandler(CreateDiscoveryJson(Issuer, $"{Issuer}/jwks"), () => CreateJwksJson(key)),
            Issuer,
            replayGuard: new NyxIdRelayReplayGuard());
        var request = CreateRelayRequest(key, messageId: "msg-replay", correlationId: "corr-replay");

        var first = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);
        var second = await validator.ValidateAsync(
            request.HttpContext,
            request.BodyBytes,
            request.Payload,
            CancellationToken.None);

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeFalse();
        second.ErrorCode.Should().Be("callback_jwt_replay_detected");
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
            new NyxIdToolOptions { BaseUrl = Issuer },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);

        var result = await client.SendChannelRelayTextReplyAsync("relay-token", "message-123", "hello", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.MessageId.Should().Be("reply-1");
        result.PlatformMessageId.Should().Be("platform-1");
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().Be($"{Issuer}/api/v1/channel-relay/reply");
        handler.LastRequest.Headers.Authorization.Should().BeEquivalentTo(new AuthenticationHeaderValue("Bearer", "relay-token"));

        handler.LastRequestBody.Should().NotBeNull();
        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        document.RootElement.GetProperty("message_id").GetString().Should().Be("message-123");
        document.RootElement.GetProperty("reply").GetProperty("text").GetString().Should().Be("hello");
    }

    private static NyxIdRelayAuthValidator CreateValidator(
        HttpMessageHandler handler,
        string baseUrl,
        INyxIdRelayReplayGuard? replayGuard = null,
        int kidMissRefreshCooldownSeconds = 0)
    {
        var factory = new NyxRelayTestHttpClientFactory(new HttpClient(handler));
        return new NyxIdRelayAuthValidator(
            factory,
            new NyxIdToolOptions { BaseUrl = baseUrl },
            new NyxIdRelayOptions
            {
                OidcCacheTtlSeconds = 60,
                JwtClockSkewSeconds = 60,
                RequireMessageIdHeader = true,
                JwksKidMissRefreshCooldownSeconds = kidMissRefreshCooldownSeconds,
            },
            NullLogger<NyxIdRelayAuthValidator>.Instance,
            replayGuard);
    }

    private static RelayRequest CreateRelayRequest(
        RsaSecurityKey signingKey,
        string issuer = Issuer,
        string audience = CallbackAudience,
        string subject = "scope-123",
        string agentApiKeyId = "api-key-123",
        string? payloadAgentApiKeyId = null,
        string messageId = "msg-1",
        string? tokenMessageId = null,
        string platform = "lark",
        string? tokenPlatform = null,
        string correlationId = "corr-1",
        string? tokenJti = null,
        string? callbackTokenOverride = null,
        bool includeCallbackToken = true,
        string? userToken = "user-token-1",
        DateTime? notBeforeUtc = null,
        DateTime? expiresAtUtc = null,
        string signingAlgorithm = SecurityAlgorithms.RsaSha256)
    {
        var payload = new NyxIdRelayCallbackPayload
        {
            MessageId = messageId,
            CorrelationId = correlationId,
            Platform = platform,
            Agent = new NyxIdRelayAgentPayload
            {
                ApiKeyId = payloadAgentApiKeyId ?? agentApiKeyId,
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
        var callbackToken = callbackTokenOverride ?? CreateCallbackJwt(
            signingKey,
            issuer,
            audience,
            subject,
            agentApiKeyId,
            tokenMessageId ?? messageId,
            tokenPlatform ?? platform,
            tokenJti ?? correlationId,
            ComputeBodySha256Hex(bodyBytes),
            notBeforeUtc,
            expiresAtUtc,
            signingAlgorithm);

        var http = new DefaultHttpContext();
        if (includeCallbackToken)
            http.Request.Headers["X-NyxID-Callback-Token"] = callbackToken;
        if (!string.IsNullOrWhiteSpace(userToken))
            http.Request.Headers["X-NyxID-User-Token"] = userToken;
        http.Request.Headers["X-NyxID-Message-Id"] = messageId;

        return new RelayRequest(http, bodyBytes, payload);
    }

    private static string CreateCallbackJwt(
        RsaSecurityKey key,
        string issuer,
        string audience,
        string subject,
        string relayApiKeyId,
        string messageId,
        string platform,
        string jti,
        string bodySha256,
        DateTime? notBeforeUtc = null,
        DateTime? expiresAtUtc = null,
        string signingAlgorithm = SecurityAlgorithms.RsaSha256)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", subject),
                new Claim("api_key_id", relayApiKeyId),
                new Claim("message_id", messageId),
                new Claim("platform", platform),
                new Claim("body_sha256", bodySha256),
                new Claim(JwtRegisteredClaimNames.Jti, jti),
            ]),
            NotBefore = notBeforeUtc ?? DateTime.UtcNow.AddMinutes(-1),
            Expires = expiresAtUtc ?? DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, signingAlgorithm),
        };

        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }

    private static RsaSecurityKey CreateSigningKey(RSA rsa, string keyId) =>
        new(rsa)
        {
            KeyId = keyId,
        };

    private static string ComputeBodySha256Hex(byte[] bodyBytes) =>
        Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();

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

    private sealed class RelayMetricCapture : IDisposable
    {
        private readonly MeterListener _listener = new();

        public RelayMetricCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == NyxIdRelayMetrics.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                if (instrument.Name == NyxIdRelayMetrics.CallbackJwtValidationFailuresTotal)
                    Failures.Add(new MetricMeasurement(measurement, tags.ToArray()));
            });
            _listener.Start();
        }

        public List<MetricMeasurement> Failures { get; } = [];

        public void Dispose() => _listener.Dispose();
    }

    private sealed record MetricMeasurement(long Value, KeyValuePair<string, object?>[] Tags);

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
