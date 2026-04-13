using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using Aevatar.GAgents.ChannelRuntime.Adapters;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public class LarkPlatformAdapterTests
{
    private readonly LarkPlatformAdapter _adapter = new(NullLogger<LarkPlatformAdapter>.Instance);

    private static ChannelBotRegistrationEntry MakeRegistration() => new()
    {
        Id = "test-reg-1",
        Platform = "lark",
        NyxProviderSlug = "api-lark-bot",
        NyxUserToken = "test-token",
        VerificationToken = "verify-token",
        ScopeId = "test-scope",
        CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    };

    private static HttpContext CreateHttpContext(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Request.ContentType = "application/json";
        context.Request.EnableBuffering();
        return context;
    }

    [Fact]
    public void Platform_returns_lark()
    {
        _adapter.Platform.Should().Be("lark");
    }

    [Fact]
    public async Task TryHandleVerification_returns_challenge_for_url_verification()
    {
        var payload = new
        {
            type = "url_verification",
            challenge = "test-challenge-123",
            token = "verify-token",
        };

        var http = CreateHttpContext(payload);
        var result = await _adapter.TryHandleVerificationAsync(http, MakeRegistration());

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task TryHandleVerification_rejects_mismatched_token()
    {
        var payload = new
        {
            type = "url_verification",
            challenge = "test-challenge-123",
            token = "wrong-token",
        };

        var http = CreateHttpContext(payload);
        var result = await _adapter.TryHandleVerificationAsync(http, MakeRegistration());

        // Should return an IResult (Unauthorized), not null
        result.Should().NotBeNull();
        // The result should be an UnauthorizedHttpResult (401)
        result.Should().BeOfType(typeof(Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult));
    }

    [Fact]
    public async Task TryHandleVerification_accepts_matching_token()
    {
        var payload = new
        {
            type = "url_verification",
            challenge = "test-challenge-ok",
            token = "verify-token",
        };

        var http = CreateHttpContext(payload);
        var result = await _adapter.TryHandleVerificationAsync(http, MakeRegistration());

        // Should return a JsonHttpResult (the challenge echo), not Unauthorized
        result.Should().NotBeNull();
        result.Should().NotBeOfType(typeof(Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult));
    }

    [Fact]
    public async Task TryHandleVerification_allows_when_no_verification_token_configured()
    {
        var payload = new
        {
            type = "url_verification",
            challenge = "test-challenge-no-verify",
            token = "any-token",
        };

        // Registration with empty verification token — should skip check
        var reg = new ChannelBotRegistrationEntry
        {
            Id = "test-reg-no-verify",
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            NyxUserToken = "test-token",
            VerificationToken = "",
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        var http = CreateHttpContext(payload);
        var result = await _adapter.TryHandleVerificationAsync(http, reg);

        result.Should().NotBeNull();
        result.Should().NotBeOfType(typeof(Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult));
    }

    [Fact]
    public async Task ParseInbound_rejects_mismatched_event_token()
    {
        var payload = new
        {
            schema = "2.0",
            header = new { event_type = "im.message.receive_v1", token = "wrong-token" },
            @event = new
            {
                sender = new { sender_id = new { open_id = "ou_abc" }, sender_type = "user" },
                message = new
                {
                    chat_id = "oc_chat1",
                    message_type = "text",
                    content = JsonSerializer.Serialize(new { text = "hello" }),
                },
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task TryHandleVerification_returns_null_for_normal_event()
    {
        var payload = new
        {
            schema = "2.0",
            header = new { event_type = "im.message.receive_v1" },
            @event = new { message = new { chat_id = "oc_123", content = "{\"text\":\"hi\"}", message_type = "text" } },
        };

        var http = CreateHttpContext(payload);
        var result = await _adapter.TryHandleVerificationAsync(http, MakeRegistration());

        result.Should().BeNull();
    }

    [Fact]
    public async Task ParseInbound_extracts_text_message()
    {
        var payload = new
        {
            schema = "2.0",
            header = new { event_type = "im.message.receive_v1", token = "verify-token" },
            @event = new
            {
                sender = new
                {
                    sender_id = new { open_id = "ou_abc123" },
                    sender_type = "user",
                },
                message = new
                {
                    chat_id = "oc_chat456",
                    message_id = "om_msg789",
                    message_type = "text",
                    chat_type = "p2p",
                    content = JsonSerializer.Serialize(new { text = "Hello from Lark!" }),
                },
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().NotBeNull();
        inbound!.Platform.Should().Be("lark");
        inbound.ConversationId.Should().Be("oc_chat456");
        inbound.SenderId.Should().Be("ou_abc123");
        inbound.Text.Should().Be("Hello from Lark!");
        inbound.MessageId.Should().Be("om_msg789");
        inbound.ChatType.Should().Be("p2p");
    }

    [Fact]
    public async Task ParseInbound_returns_null_for_non_message_event()
    {
        var payload = new
        {
            schema = "2.0",
            header = new { event_type = "im.chat.member.bot.added_v1" },
            @event = new { },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task ParseInbound_returns_null_for_empty_text()
    {
        var payload = new
        {
            schema = "2.0",
            header = new { event_type = "im.message.receive_v1" },
            @event = new
            {
                sender = new { sender_id = new { open_id = "ou_abc" } },
                message = new
                {
                    chat_id = "oc_chat1",
                    message_type = "image",
                    content = "{}",
                },
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task ParseInbound_ignores_bot_sender()
    {
        var payload = new
        {
            schema = "2.0",
            header = new { event_type = "im.message.receive_v1" },
            @event = new
            {
                sender = new
                {
                    sender_id = new { open_id = "ou_bot" },
                    sender_type = "bot",
                },
                message = new
                {
                    chat_id = "oc_chat1",
                    message_type = "text",
                    content = JsonSerializer.Serialize(new { text = "bot message" }),
                },
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task ParseInbound_returns_null_when_missing_header()
    {
        var payload = new { schema = "2.0" };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task SendReplyAsync_returns_success_detail_when_lark_accepts_message()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.OK, """
            {"code":0,"msg":"success","data":{"message_id":"om_success_123"}}
            """);
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var inbound = new InboundMessage
        {
            Platform = "lark",
            ConversationId = "oc_chat_123",
            SenderId = "ou_sender_1",
            SenderName = "sender-1",
            Text = "hello",
        };

        var result = await _adapter.SendReplyAsync("reply", inbound, MakeRegistration(), nyxClient, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Detail.Should().Be("message_id=om_success_123");
    }

    [Fact]
    public async Task SendReplyAsync_returns_failed_result_when_lark_rejects_message()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.OK, """
            {"code":230001,"msg":"invalid receive id"}
            """);
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var inbound = new InboundMessage
        {
            Platform = "lark",
            ConversationId = "oc_chat_123",
            SenderId = "ou_sender_1",
            SenderName = "sender-1",
            Text = "hello",
        };

        var result = await _adapter.SendReplyAsync("reply", inbound, MakeRegistration(), nyxClient, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Be("lark_code=230001 msg=invalid receive id");
    }

    private static HttpClient CreateHttpClient(HttpStatusCode statusCode, string body)
    {
        return new HttpClient(new StaticResponseHandler(statusCode, body))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
    }

    // ─── Signature Verification Tests ───

    [Fact]
    public async Task ParseInbound_verifies_signature_when_encrypt_key_configured()
    {
        var encryptKey = "test-encrypt-key-123";
        var reg = MakeRegistrationWithEncryptKey(encryptKey);

        var payload = BuildLarkV2Payload("im.message.receive_v1", "1234567890", "test-nonce");
        var bodyString = JsonSerializer.Serialize(payload);
        var expectedSignature = LarkPlatformAdapter.ComputeLarkSignature(
            "1234567890", "test-nonce", encryptKey, bodyString);

        var http = CreateHttpContextWithSignature(bodyString, expectedSignature);
        var inbound = await _adapter.ParseInboundAsync(http, reg);

        inbound.Should().NotBeNull();
        inbound!.Text.Should().Be("Hello from Lark!");
    }

    [Fact]
    public async Task ParseInbound_rejects_invalid_signature()
    {
        var encryptKey = "test-encrypt-key-123";
        var reg = MakeRegistrationWithEncryptKey(encryptKey);

        var payload = BuildLarkV2Payload("im.message.receive_v1", "1234567890", "test-nonce");
        var bodyString = JsonSerializer.Serialize(payload);

        var http = CreateHttpContextWithSignature(bodyString, "invalid-signature-value");
        var inbound = await _adapter.ParseInboundAsync(http, reg);

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task ParseInbound_rejects_missing_signature_header_when_encrypt_key_configured()
    {
        var encryptKey = "test-encrypt-key-123";
        var reg = MakeRegistrationWithEncryptKey(encryptKey);

        // Valid payload but NO X-Lark-Signature header → must be rejected
        var payload = BuildLarkV2Payload("im.message.receive_v1", "1234567890", "test-nonce");
        var bodyString = JsonSerializer.Serialize(payload);
        var http = CreateHttpContext(payload); // no signature header
        var inbound = await _adapter.ParseInboundAsync(http, reg);

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task ParseInbound_skips_signature_when_no_encrypt_key()
    {
        // No encrypt_key → falls back to token verification
        var reg = MakeRegistration();
        var payload = new
        {
            schema = "2.0",
            header = new
            {
                event_type = "im.message.receive_v1",
                token = "verify-token",
                create_time = "1234567890",
                nonce = "some-nonce",
            },
            @event = new
            {
                sender = new { sender_id = new { open_id = "ou_abc" }, sender_type = "user" },
                message = new
                {
                    chat_id = "oc_chat1",
                    message_id = "om_msg1",
                    message_type = "text",
                    content = JsonSerializer.Serialize(new { text = "hello" }),
                },
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, reg);

        inbound.Should().NotBeNull();
    }

    // ─── Encryption Tests ───

    [Fact]
    public void ComputeLarkSignature_produces_consistent_hex()
    {
        var result = LarkPlatformAdapter.ComputeLarkSignature("ts", "nonce", "key", "body");

        result.Should().NotBeNullOrEmpty();
        result.Should().MatchRegex("^[0-9a-f]+$");
        // Same inputs → same output
        var result2 = LarkPlatformAdapter.ComputeLarkSignature("ts", "nonce", "key", "body");
        result.Should().Be(result2);
    }

    [Fact]
    public void DecryptLarkPayload_round_trips()
    {
        var encryptKey = "round-trip-test-key";
        var plaintext = """{"type":"url_verification","challenge":"abc123"}""";
        var encrypted = EncryptLarkPayload(plaintext, encryptKey);

        var decrypted = LarkPlatformAdapter.DecryptLarkPayload(encrypted, encryptKey);

        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public async Task TryHandleVerification_decrypts_encrypted_challenge()
    {
        var encryptKey = "verify-encrypt-key";
        var reg = MakeRegistrationWithEncryptKey(encryptKey);

        var innerPayload = """{"type":"url_verification","challenge":"encrypted-challenge-abc"}""";
        var encrypted = EncryptLarkPayload(innerPayload, encryptKey);

        var outerPayload = new { encrypt = encrypted };
        var http = CreateHttpContext(outerPayload);
        var result = await _adapter.TryHandleVerificationAsync(http, reg);

        result.Should().NotBeNull();
        result.Should().NotBeOfType(typeof(Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult));
    }

    [Fact]
    public async Task ParseInbound_decrypts_encrypted_event_payload()
    {
        var encryptKey = "event-encrypt-key";
        var reg = MakeRegistrationWithEncryptKey(encryptKey);

        var innerPayload = JsonSerializer.Serialize(new
        {
            schema = "2.0",
            header = new
            {
                event_type = "im.message.receive_v1",
                token = "verify-token",
                create_time = "1234567890",
                nonce = "test-nonce",
            },
            @event = new
            {
                sender = new { sender_id = new { open_id = "ou_enc_user" }, sender_type = "user" },
                message = new
                {
                    chat_id = "oc_enc_chat",
                    message_id = "om_enc_msg",
                    message_type = "text",
                    content = JsonSerializer.Serialize(new { text = "encrypted message" }),
                },
            },
        });

        var encrypted = EncryptLarkPayload(innerPayload, encryptKey);
        var outerPayloadJson = JsonSerializer.Serialize(new { encrypt = encrypted });

        // Signature is computed on the ORIGINAL (encrypted) body
        var signature = LarkPlatformAdapter.ComputeLarkSignature(
            "1234567890", "test-nonce", encryptKey, outerPayloadJson);

        var http = CreateHttpContextWithSignature(outerPayloadJson, signature);
        var inbound = await _adapter.ParseInboundAsync(http, reg);

        inbound.Should().NotBeNull();
        inbound!.Text.Should().Be("encrypted message");
        inbound.SenderId.Should().Be("ou_enc_user");
        inbound.ConversationId.Should().Be("oc_enc_chat");
    }

    [Fact]
    public async Task TryHandleVerification_rejects_bad_encrypted_payload()
    {
        var reg = MakeRegistrationWithEncryptKey("some-key");

        // Invalid base64 in encrypt field
        var outerPayload = new { encrypt = "not-valid-base64!!!" };
        var http = CreateHttpContext(outerPayload);
        var result = await _adapter.TryHandleVerificationAsync(http, reg);

        // Should return Unauthorized due to decryption failure
        result.Should().NotBeNull();
        result.Should().BeOfType(typeof(Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult));
    }

    // ─── Test Helpers ───

    private static ChannelBotRegistrationEntry MakeRegistrationWithEncryptKey(string encryptKey) => new()
    {
        Id = "test-reg-enc",
        Platform = "lark",
        NyxProviderSlug = "api-lark-bot",
        NyxUserToken = "test-token",
        VerificationToken = "verify-token",
        EncryptKey = encryptKey,
        ScopeId = "test-scope",
        CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    };

    private static object BuildLarkV2Payload(string eventType, string createTime, string nonce) => new
    {
        schema = "2.0",
        header = new
        {
            event_type = eventType,
            token = "verify-token",
            create_time = createTime,
            nonce,
        },
        @event = new
        {
            sender = new { sender_id = new { open_id = "ou_abc123" }, sender_type = "user" },
            message = new
            {
                chat_id = "oc_chat456",
                message_id = "om_msg789",
                message_type = "text",
                chat_type = "p2p",
                content = JsonSerializer.Serialize(new { text = "Hello from Lark!" }),
            },
        },
    };

    private static HttpContext CreateHttpContextWithSignature(string bodyJson, string signature)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(bodyJson));
        context.Request.ContentType = "application/json";
        context.Request.EnableBuffering();
        context.Request.Headers["X-Lark-Signature"] = signature;
        return context;
    }

    /// <summary>
    /// Encrypt plaintext using Lark's protocol: AES-256-CBC, key = SHA256(encrypt_key),
    /// IV = random 16 bytes prepended to ciphertext, result is base64.
    /// </summary>
    private static string EncryptLarkPayload(string plaintext, string encryptKey)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(encryptKey));
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        // Prepend IV to ciphertext (Lark protocol)
        var result = new byte[aes.IV.Length + encrypted.Length];
        aes.IV.CopyTo(result, 0);
        encrypted.CopyTo(result, aes.IV.Length);
        return Convert.ToBase64String(result);
    }

    private sealed class StaticResponseHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
