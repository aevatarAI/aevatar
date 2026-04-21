using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using Aevatar.GAgents.ChannelRuntime.Adapters;
using Aevatar.Foundation.Abstractions.HumanInteraction;
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
    public async Task ParseInbound_extracts_card_action_with_resume_fields()
    {
        var payload = new
        {
            schema = "2.0",
            header = new { event_type = "card.action.trigger", token = "verify-token", event_id = "evt_card_123" },
            @event = new
            {
                @operator = new
                {
                    open_id = "ou_operator_1",
                },
                context = new
                {
                    open_chat_id = "oc_chat_card_1",
                    open_message_id = "om_card_msg_1",
                },
                action = new
                {
                    value = new
                    {
                        actor_id = "run-actor-1",
                        run_id = "run-1",
                        step_id = "approval-1",
                        approved = true,
                    },
                    form_value = new
                    {
                        user_input = "Looks good",
                    },
                },
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().NotBeNull();
        inbound!.ChatType.Should().Be("card_action");
        inbound.ConversationId.Should().Be("oc_chat_card_1");
        inbound.SenderId.Should().Be("ou_operator_1");
        inbound.MessageId.Should().Be("evt_card_123");
        inbound.Extra.Should().Contain(new KeyValuePair<string, string>("actor_id", "run-actor-1"));
        inbound.Extra.Should().Contain(new KeyValuePair<string, string>("run_id", "run-1"));
        inbound.Extra.Should().Contain(new KeyValuePair<string, string>("step_id", "approval-1"));
        inbound.Extra.Should().Contain(new KeyValuePair<string, string>("approved", "True"));
        inbound.Extra.Should().Contain(new KeyValuePair<string, string>("user_input", "Looks good"));
    }

    [Fact]
    public async Task ParseInbound_extracts_resume_command_from_rendered_approval_form_submit()
    {
        var cardJson = FeishuCardHumanInteractionPort.BuildCardJson(new HumanInteractionRequest
        {
            ActorId = "workflow-actor-1",
            RunId = "run-1",
            StepId = "approval-1",
            SuspensionType = "human_approval",
            Prompt = "Need approval",
            Content = "Please confirm the publication.",
            Options = ["approve", "reject"],
        });

        using var card = JsonDocument.Parse(cardJson);
        var formElement = card.RootElement.GetProperty("body").GetProperty("elements")[1];
        var approveActionValue = formElement.GetProperty("elements")[2].GetProperty("actions")[0].GetProperty("value").Clone();

        var payload = new
        {
            schema = "2.0",
            header = new { event_type = "card.action.trigger", token = "verify-token", event_id = "evt_card_submit_1" },
            @event = new
            {
                @operator = new
                {
                    open_id = "ou_operator_1",
                },
                context = new
                {
                    open_chat_id = "oc_chat_card_1",
                    open_message_id = "om_card_msg_1",
                },
                action = new
                {
                    value = approveActionValue,
                    form_value = new
                    {
                        edited_content = "Updated final draft",
                        user_input = "Looks good",
                    },
                },
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().NotBeNull();
        ChannelCardActionRouting.TryBuildWorkflowResumeCommand(inbound!, out var command).Should().BeTrue();
        command.Should().NotBeNull();
        command!.ActorId.Should().Be("workflow-actor-1");
        command.RunId.Should().Be("run-1");
        command.StepId.Should().Be("approval-1");
        command.Approved.Should().BeTrue();
        command.UserInput.Should().Be("Updated final draft");
        command.EditedContent.Should().Be("Updated final draft");
        command.Feedback.Should().Be("Looks good");
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
            """, out _);
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
            """, out _);
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

    [Fact]
    public async Task SendReplyAsync_uses_interactive_message_type_for_card_payload()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.OK, """
            {"code":0,"msg":"success","data":{"message_id":"om_card_123"}}
            """, out var handler);
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
        const string replyText = """
            {"header":{"title":{"tag":"plain_text","content":"Approval"}},"elements":[]}
            """;

        var result = await _adapter.SendReplyAsync(replyText, inbound, MakeRegistration(), nyxClient, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        handler.LastRequestBody.Should().Contain("\"msg_type\":\"interactive\"");
        handler.LastRequestBody.Should().Contain("\\u0022header\\u0022");
    }

    [Fact]
    public void IsInteractiveCardPayload_detects_feishu_card_json()
    {
        LarkPlatformAdapter.IsInteractiveCardPayload("""{"elements":[]}""").Should().BeTrue();
        LarkPlatformAdapter.IsInteractiveCardPayload("hello").Should().BeFalse();
    }

    private static HttpClient CreateHttpClient(HttpStatusCode statusCode, string body, out StaticResponseHandler handler)
    {
        handler = new StaticResponseHandler(statusCode, body);
        return new HttpClient(handler)
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
        const string timestamp = "1234567890";
        const string nonce = "test-nonce";

        var payload = BuildLarkV2Payload("im.message.receive_v1", timestamp, nonce);
        var bodyString = JsonSerializer.Serialize(payload);
        var expectedSignature = LarkPlatformAdapter.ComputeLarkSignature(
            timestamp, nonce, encryptKey, bodyString);

        var http = CreateHttpContextWithSignature(bodyString, expectedSignature, timestamp, nonce);
        var inbound = await _adapter.ParseInboundAsync(http, reg);

        inbound.Should().NotBeNull();
        inbound!.Text.Should().Be("Hello from Lark!");
    }

    [Fact]
    public async Task ParseInbound_rejects_invalid_signature()
    {
        var encryptKey = "test-encrypt-key-123";
        var reg = MakeRegistrationWithEncryptKey(encryptKey);
        const string timestamp = "1234567890";
        const string nonce = "test-nonce";

        var payload = BuildLarkV2Payload("im.message.receive_v1", timestamp, nonce);
        var bodyString = JsonSerializer.Serialize(payload);

        var http = CreateHttpContextWithSignature(bodyString, "invalid-signature-value", timestamp, nonce);
        var inbound = await _adapter.ParseInboundAsync(http, reg);

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task ParseInbound_signature_uses_request_headers_not_json_header_fields()
    {
        var encryptKey = "test-encrypt-key-123";
        var reg = MakeRegistrationWithEncryptKey(encryptKey);
        const string jsonTimestamp = "json-ts";
        const string jsonNonce = "json-nonce";
        const string requestTimestamp = "request-ts";
        const string requestNonce = "request-nonce";

        var payload = BuildLarkV2Payload("im.message.receive_v1", jsonTimestamp, jsonNonce);
        var bodyString = JsonSerializer.Serialize(payload);
        var expectedSignature = LarkPlatformAdapter.ComputeLarkSignature(
            requestTimestamp, requestNonce, encryptKey, bodyString);

        var http = CreateHttpContextWithSignature(bodyString, expectedSignature, requestTimestamp, requestNonce);
        var inbound = await _adapter.ParseInboundAsync(http, reg);

        inbound.Should().NotBeNull();
        inbound!.Text.Should().Be("Hello from Lark!");
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
        const string timestamp = "1234567890";
        const string nonce = "test-nonce";
        var signature = LarkPlatformAdapter.ComputeLarkSignature(
            timestamp, nonce, encryptKey, outerPayloadJson);

        var http = CreateHttpContextWithSignature(outerPayloadJson, signature, timestamp, nonce);
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

    // ─── Signature Edge Cases ───

    [Fact]
    public void ComputeLarkSignature_different_inputs_produce_different_hashes()
    {
        var sig1 = LarkPlatformAdapter.ComputeLarkSignature("ts1", "nonce1", "key1", "body1");
        var sig2 = LarkPlatformAdapter.ComputeLarkSignature("ts2", "nonce1", "key1", "body1");
        var sig3 = LarkPlatformAdapter.ComputeLarkSignature("ts1", "nonce2", "key1", "body1");
        var sig4 = LarkPlatformAdapter.ComputeLarkSignature("ts1", "nonce1", "key2", "body1");
        var sig5 = LarkPlatformAdapter.ComputeLarkSignature("ts1", "nonce1", "key1", "body2");

        new[] { sig1, sig2, sig3, sig4, sig5 }.Distinct().Should().HaveCount(5);
    }

    [Fact]
    public void ComputeLarkSignature_empty_inputs_still_produces_valid_hex()
    {
        var result = LarkPlatformAdapter.ComputeLarkSignature("", "", "", "");
        result.Should().NotBeNullOrEmpty();
        result.Should().MatchRegex("^[0-9a-f]{64}$"); // SHA256 = 64 hex chars
    }

    [Fact]
    public async Task ParseInbound_signature_uses_original_encrypted_body_not_decrypted()
    {
        // Verify that signature is computed on the OUTER encrypted payload, not the inner decrypted content.
        // If the adapter used decrypted body for signature, this test would fail.
        var encryptKey = "sig-body-test-key";
        var reg = MakeRegistrationWithEncryptKey(encryptKey);

        var innerPayload = JsonSerializer.Serialize(new
        {
            schema = "2.0",
            header = new
            {
                event_type = "im.message.receive_v1",
                token = "verify-token",
                create_time = "9999999999",
                nonce = "unique-nonce-42",
            },
            @event = new
            {
                sender = new { sender_id = new { open_id = "ou_sig_test" }, sender_type = "user" },
                message = new
                {
                    chat_id = "oc_sig_chat",
                    message_id = "om_sig_msg",
                    message_type = "text",
                    content = JsonSerializer.Serialize(new { text = "signature body test" }),
                },
            },
        });

        var encrypted = EncryptLarkPayload(innerPayload, encryptKey);
        var outerPayloadJson = JsonSerializer.Serialize(new { encrypt = encrypted });
        const string timestamp = "9999999999";
        const string nonce = "unique-nonce-42";

        // Correct: signature on outer (encrypted) body
        var correctSignature = LarkPlatformAdapter.ComputeLarkSignature(
            timestamp, nonce, encryptKey, outerPayloadJson);
        // Wrong: signature on inner (decrypted) body
        var wrongSignature = LarkPlatformAdapter.ComputeLarkSignature(
            timestamp, nonce, encryptKey, innerPayload);

        // Correct signature should work
        var httpGood = CreateHttpContextWithSignature(outerPayloadJson, correctSignature, timestamp, nonce);
        var resultGood = await _adapter.ParseInboundAsync(httpGood, reg);
        resultGood.Should().NotBeNull();

        // Wrong signature (computed on decrypted body) should be rejected
        var httpBad = CreateHttpContextWithSignature(outerPayloadJson, wrongSignature, timestamp, nonce);
        var resultBad = await _adapter.ParseInboundAsync(httpBad, reg);
        resultBad.Should().BeNull();
    }

    [Fact]
    public async Task ParseInbound_rejects_empty_signature_header_when_encrypt_key_configured()
    {
        var encryptKey = "test-key";
        var reg = MakeRegistrationWithEncryptKey(encryptKey);

        var payload = BuildLarkV2Payload("im.message.receive_v1", "123", "nonce");
        var bodyString = JsonSerializer.Serialize(payload);

        // Set header to empty string instead of omitting it
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(bodyString));
        context.Request.ContentType = "application/json";
        context.Request.EnableBuffering();
        context.Request.Headers["X-Lark-Signature"] = "";

        var inbound = await _adapter.ParseInboundAsync(context, reg);
        inbound.Should().BeNull();
    }

    // ─── Decryption Edge Cases ───

    [Fact]
    public void DecryptLarkPayload_throws_on_truncated_ciphertext()
    {
        // Ciphertext shorter than 16 bytes (IV length) should throw
        var shortCiphertext = Convert.ToBase64String(new byte[10]);

        var act = () => LarkPlatformAdapter.DecryptLarkPayload(shortCiphertext, "any-key");
        act.Should().Throw<System.Security.Cryptography.CryptographicException>()
            .WithMessage("*too short*");
    }

    [Fact]
    public void DecryptLarkPayload_throws_on_wrong_key()
    {
        var plaintext = """{"type":"test"}""";
        var encrypted = EncryptLarkPayload(plaintext, "correct-key");

        // Decrypting with wrong key should throw (PKCS7 padding error)
        var act = () => LarkPlatformAdapter.DecryptLarkPayload(encrypted, "wrong-key");
        act.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }

    [Fact]
    public void DecryptLarkPayload_handles_large_payload()
    {
        var largePlaintext = new string('x', 10_000);
        var encrypted = EncryptLarkPayload(largePlaintext, "large-key");

        var decrypted = LarkPlatformAdapter.DecryptLarkPayload(encrypted, "large-key");
        decrypted.Should().Be(largePlaintext);
    }

    [Fact]
    public void DecryptLarkPayload_handles_unicode_content()
    {
        var unicodePayload = """{"text":"你好世界 🎉 مرحبا"}""";
        var encrypted = EncryptLarkPayload(unicodePayload, "unicode-key");

        var decrypted = LarkPlatformAdapter.DecryptLarkPayload(encrypted, "unicode-key");
        decrypted.Should().Be(unicodePayload);
    }

    [Fact]
    public async Task ParseInbound_encrypted_payload_returns_null_when_decrypt_fails()
    {
        var reg = MakeRegistrationWithEncryptKey("my-key");

        // Encrypted with a different key
        var innerPayload = JsonSerializer.Serialize(new
        {
            schema = "2.0",
            header = new { event_type = "im.message.receive_v1", create_time = "1", nonce = "n" },
            @event = new
            {
                sender = new { sender_id = new { open_id = "ou_x" }, sender_type = "user" },
                message = new
                {
                    chat_id = "oc_1", message_type = "text",
                    content = JsonSerializer.Serialize(new { text = "secret" }),
                },
            },
        });
        var encrypted = EncryptLarkPayload(innerPayload, "different-key");
        var outerPayloadJson = JsonSerializer.Serialize(new { encrypt = encrypted });

        var http = CreateHttpContext(new { encrypt = encrypted });
        var inbound = await _adapter.ParseInboundAsync(http, reg);

        inbound.Should().BeNull();
    }

    // ─── Encrypted URL Verification Edge Cases ───

    [Fact]
    public async Task TryHandleVerification_encrypted_non_verification_payload_returns_null()
    {
        var encryptKey = "test-key";
        var reg = MakeRegistrationWithEncryptKey(encryptKey);

        // Encrypt a non-verification payload
        var innerPayload = """{"schema":"2.0","header":{"event_type":"im.message.receive_v1"}}""";
        var encrypted = EncryptLarkPayload(innerPayload, encryptKey);

        var http = CreateHttpContext(new { encrypt = encrypted });
        var result = await _adapter.TryHandleVerificationAsync(http, reg);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryHandleVerification_ignores_encrypt_field_when_no_encrypt_key()
    {
        // Registration without encrypt_key — should not attempt decryption
        var reg = MakeRegistration();
        var http = CreateHttpContext(new { encrypt = "some-encrypted-data" });
        var result = await _adapter.TryHandleVerificationAsync(http, reg);

        // Should return null (not a verification, and no decrypt attempted)
        result.Should().BeNull();
    }

    // ─── SendReply Edge Cases ───

    [Fact]
    public async Task SendReplyAsync_returns_failed_for_empty_response()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.OK, "", out _);
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var inbound = new InboundMessage
        {
            Platform = "lark", ConversationId = "oc_1",
            SenderId = "ou_1", SenderName = "s", Text = "hi",
        };

        var result = await _adapter.SendReplyAsync("reply", inbound, MakeRegistration(), nyxClient, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Contain("empty");
    }

    [Fact]
    public async Task SendReplyAsync_returns_failed_result_when_proxy_reports_permanent_lark_error()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.BadRequest, """
            {"code":230002,"msg":"Bot/User can NOT be out of the chat."}
            """, out _);
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var inbound = new InboundMessage
        {
            Platform = "lark", ConversationId = "oc_1",
            SenderId = "ou_1", SenderName = "s", Text = "hi",
        };

        var result = await _adapter.SendReplyAsync("reply", inbound, MakeRegistration(), nyxClient, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Be("lark_code=230002 msg=Bot/User can NOT be out of the chat.");
        result.FailureKind.Should().Be(PlatformReplyFailureKind.Permanent);
    }

    [Fact]
    public async Task SendReplyAsync_returns_failed_result_when_proxy_reports_token_expired()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.Unauthorized, """
            {"error":"token_expired","error_code":2001,"message":"Token expired"}
            """, out _);
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var inbound = new InboundMessage
        {
            Platform = "lark", ConversationId = "oc_1",
            SenderId = "ou_1", SenderName = "s", Text = "hi",
        };

        var result = await _adapter.SendReplyAsync("reply", inbound, MakeRegistration(), nyxClient, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Be("nyx_status=401 lark_error=token_expired error_code=2001 message=Token expired");
        result.FailureKind.Should().Be(PlatformReplyFailureKind.Permanent);
    }

    [Fact]
    public async Task SendReplyAsync_classifies_failure_when_lark_body_uses_boolean_error_field()
    {
        // Nyx wraps a non-2xx upstream whose body is {"error": true, "message": "..."}.
        // The inner `error` is a JSON boolean, not a string, and must not throw.
        var httpClient = CreateHttpClient(HttpStatusCode.BadRequest, """
            {"error":true,"message":"upstream refused"}
            """, out _);
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var inbound = new InboundMessage
        {
            Platform = "lark", ConversationId = "oc_1",
            SenderId = "ou_1", SenderName = "s", Text = "hi",
        };

        var result = await _adapter.SendReplyAsync("reply", inbound, MakeRegistration(), nyxClient, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Be("nyx_status=400 message=upstream refused");
        result.FailureKind.Should().Be(PlatformReplyFailureKind.Permanent);
    }

    [Fact]
    public async Task SendReplyAsync_preserves_proxy_exception_message_and_marks_it_transient()
    {
        var httpClient = new HttpClient(new ThrowingHandler(new HttpRequestException("dns failure")))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var inbound = new InboundMessage
        {
            Platform = "lark", ConversationId = "oc_1",
            SenderId = "ou_1", SenderName = "s", Text = "hi",
        };

        var result = await _adapter.SendReplyAsync("reply", inbound, MakeRegistration(), nyxClient, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Be("nyx_error status=unknown message=dns failure");
        result.FailureKind.Should().Be(PlatformReplyFailureKind.Transient);
    }

    [Fact]
    public async Task SendReplyAsync_returns_result_length_when_no_message_id_in_response()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.OK, """{"code":0,"msg":"success","data":{}}""", out _);
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var inbound = new InboundMessage
        {
            Platform = "lark", ConversationId = "oc_1",
            SenderId = "ou_1", SenderName = "s", Text = "hi",
        };

        var result = await _adapter.SendReplyAsync("reply", inbound, MakeRegistration(), nyxClient, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Detail.Should().StartWith("result_length=");
    }

    [Fact]
    public async Task ParseInbound_returns_null_when_chat_id_missing()
    {
        var payload = new
        {
            schema = "2.0",
            header = new { event_type = "im.message.receive_v1", token = "verify-token" },
            @event = new
            {
                sender = new { sender_id = new { open_id = "ou_abc" }, sender_type = "user" },
                message = new
                {
                    message_type = "text",
                    content = JsonSerializer.Serialize(new { text = "no chat_id" }),
                },
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task ParseInbound_group_chat_type_is_extracted()
    {
        var payload = new
        {
            schema = "2.0",
            header = new { event_type = "im.message.receive_v1", token = "verify-token" },
            @event = new
            {
                sender = new { sender_id = new { open_id = "ou_grp" }, sender_type = "user" },
                message = new
                {
                    chat_id = "oc_group_123",
                    message_id = "om_grp_1",
                    message_type = "text",
                    chat_type = "group",
                    content = JsonSerializer.Serialize(new { text = "group msg" }),
                },
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().NotBeNull();
        inbound!.ChatType.Should().Be("group");
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

    private static HttpContext CreateHttpContextWithSignature(
        string bodyJson,
        string signature,
        string timestamp = "",
        string nonce = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(bodyJson));
        context.Request.ContentType = "application/json";
        context.Request.EnableBuffering();
        context.Request.Headers["X-Lark-Signature"] = signature;
        if (!string.IsNullOrEmpty(timestamp))
            context.Request.Headers["X-Lark-Request-Timestamp"] = timestamp;
        if (!string.IsNullOrEmpty(nonce))
            context.Request.Headers["X-Lark-Request-Nonce"] = nonce;
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
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
