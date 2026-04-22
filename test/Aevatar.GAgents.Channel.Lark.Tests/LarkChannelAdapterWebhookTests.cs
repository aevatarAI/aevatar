using System.Text;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Lark;
using Aevatar.GAgents.Channel.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Aevatar.GAgents.Channel.Lark.Tests;

public sealed class LarkChannelAdapterWebhookTests
{
    [Fact]
    public async Task HandleWebhookAsync_EncryptedGroupMessage_UsesGroupCanonicalKey()
    {
        var harness = new LarkAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var innerPayload = JsonSerializer.Serialize(new
        {
            schema = "2.0",
            header = new
            {
                event_type = "im.message.receive_v1",
            },
            @event = new
            {
                sender = new
                {
                    sender_id = new
                    {
                        open_id = "user-1",
                    },
                    sender_type = "user",
                },
                message = new
                {
                    chat_id = "group-42",
                    message_id = "msg-42",
                    message_type = "text",
                    chat_type = "group",
                    content = JsonSerializer.Serialize(new
                    {
                        text = "hello group",
                    }),
                    create_time = "1710000000000",
                },
            },
        });
        var encrypted = LarkPayloadEncryption.Encrypt(innerPayload, "encrypt-key");
        var outerPayload = JsonSerializer.Serialize(new
        {
            encrypt = encrypted,
        });

        var response = await adapter.HandleWebhookAsync(new LarkWebhookRequest(
            Encoding.UTF8.GetBytes(outerPayload),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["X-Lark-Request-Timestamp"] = timestamp,
                ["X-Lark-Request-Nonce"] = "nonce",
                ["X-Lark-Signature"] = LarkChannelAdapter.ComputeLarkSignature(timestamp, "nonce", "encrypt-key", outerPayload),
            }));

        response.StatusCode.ShouldBe(200);
        response.Activity.ShouldNotBeNull();
        response.Activity.Conversation.CanonicalKey.ShouldBe("lark:group:group-42");
        response.Activity.Conversation.Scope.ShouldBe(ConversationScope.Group);
        response.Activity.RawPayloadBlobRef.ShouldStartWith("lark-raw:");
    }

    [Fact]
    public async Task SendAsync_DirectMessageReference_UsesOpenIdRouting()
    {
        var harness = new LarkAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        var result = await adapter.SendAsync(
            ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("conformance-bot"),
                ConversationScope.DirectMessage,
                partition: null,
                "user-open-id"),
            SampleMessageContent.SimpleText("hello"),
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        harness.HttpHandler.LastReceiveIdType.ShouldBe("open_id");
    }

    [Fact]
    public async Task SendAsync_RefreshesBotCredentialBeforeOutboundRequest()
    {
        var harness = new LarkAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);
        harness.CredentialProvider.Set("vault://bots/lark-conformance", JsonSerializer.Serialize(new
        {
            access_token = "rotated-access-token",
            encrypt_key = "encrypt-key",
        }));

        var result = await adapter.SendAsync(
            ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("conformance-bot"),
                ConversationScope.DirectMessage,
                partition: null,
                "user-open-id"),
            SampleMessageContent.SimpleText("hello"),
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        harness.HttpHandler.LastAuthorization.ShouldBe("rotated-access-token");
    }

    [Fact]
    public async Task HandleWebhookAsync_EncryptedPayloadWithExpiredTimestamp_Returns401()
    {
        var harness = new LarkAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);
        var expiredTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString();

        var innerPayload = JsonSerializer.Serialize(new
        {
            schema = "2.0",
            header = new
            {
                event_type = "im.message.receive_v1",
            },
            @event = new
            {
                sender = new
                {
                    sender_id = new
                    {
                        open_id = "user-1",
                    },
                    sender_type = "user",
                },
                message = new
                {
                    chat_id = "group-42",
                    message_id = "msg-42",
                    message_type = "text",
                    chat_type = "group",
                    content = JsonSerializer.Serialize(new
                    {
                        text = "hello group",
                    }),
                },
            },
        });
        var encrypted = LarkPayloadEncryption.Encrypt(innerPayload, "encrypt-key");
        var outerPayload = JsonSerializer.Serialize(new
        {
            encrypt = encrypted,
        });

        var response = await adapter.HandleWebhookAsync(new LarkWebhookRequest(
            Encoding.UTF8.GetBytes(outerPayload),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["X-Lark-Request-Timestamp"] = expiredTimestamp,
                ["X-Lark-Request-Nonce"] = "nonce",
                ["X-Lark-Signature"] = LarkChannelAdapter.ComputeLarkSignature(expiredTimestamp, "nonce", "encrypt-key", outerPayload),
            }));

        response.StatusCode.ShouldBe(401);
        response.Activity.ShouldBeNull();
    }

    [Fact]
    public async Task HandleWebhookAsync_UrlVerificationWithoutVerificationToken_FailsClosed()
    {
        var harness = new LarkAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(
            ChannelTransportBinding.Create(
                harness.DefaultBinding.Bot,
                harness.DefaultBinding.CredentialRef,
                verificationToken: string.Empty),
            CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        var payload = JsonSerializer.Serialize(new
        {
            type = "url_verification",
            token = "verify-token",
            challenge = "challenge-123",
        });

        var response = await adapter.HandleWebhookAsync(new LarkWebhookRequest(Encoding.UTF8.GetBytes(payload)));

        response.StatusCode.ShouldBe(401);
        response.ResponseBody.ShouldBeNull();
        response.Activity.ShouldBeNull();
    }

    [Fact]
    public async Task HandleWebhookAsync_CardAction_UsesTypedPayloadAndDirectMessageScope()
    {
        var harness = new LarkAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        var payload = JsonSerializer.Serialize(new
        {
            schema = "2.0",
            header = new
            {
                event_type = "card.action.trigger",
                token = harness.DefaultBinding.VerificationToken,
                event_id = "evt-card-1",
            },
            @event = new
            {
                @operator = new
                {
                    open_id = "user-open-1",
                },
                context = new
                {
                    open_chat_id = "oc_card_chat_1",
                    open_message_id = "om_card_msg_1",
                    chat_type = "p2p",
                },
                action = new
                {
                    value = new
                    {
                        action_id = "approve",
                        value = "yes",
                        actor_id = "run-actor-1",
                        run_id = "run-1",
                        approved = true,
                    },
                    form_value = new
                    {
                        note = "Looks good",
                    },
                },
            },
        });

        var response = await adapter.HandleWebhookAsync(CreateSignedRequest(payload, "encrypt-key"));

        response.StatusCode.ShouldBe(200);
        response.Activity.ShouldNotBeNull();
        response.Activity.Type.ShouldBe(ActivityType.CardAction);
        response.Activity.Conversation.Scope.ShouldBe(ConversationScope.DirectMessage);
        response.Activity.Conversation.CanonicalKey.ShouldBe("lark:dm:user-open-1");
        response.Activity.Content.Text.ShouldBeEmpty();
        response.Activity.Content.CardAction.ActionId.ShouldBe("approve");
        response.Activity.Content.CardAction.SubmittedValue.ShouldBe("yes");
        response.Activity.Content.CardAction.SourceMessageId.ShouldBe("om_card_msg_1");
        response.Activity.Content.CardAction.Arguments["actor_id"].ShouldBe("run-actor-1");
        response.Activity.Content.CardAction.Arguments["run_id"].ShouldBe("run-1");
        response.Activity.Content.CardAction.Arguments["approved"].ShouldBe(bool.TrueString);
        response.Activity.Content.CardAction.FormFields["note"].ShouldBe("Looks good");
    }

    [Fact]
    public async Task HandleWebhookAsync_CardActionWithoutChatType_DropsCallback()
    {
        var harness = new LarkAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        var payload = JsonSerializer.Serialize(new
        {
            schema = "2.0",
            header = new
            {
                event_type = "card.action.trigger",
                token = harness.DefaultBinding.VerificationToken,
                event_id = "evt-card-1",
            },
            @event = new
            {
                @operator = new
                {
                    open_id = "user-open-1",
                },
                context = new
                {
                    open_chat_id = "oc_card_chat_1",
                    open_message_id = "om_card_msg_1",
                },
                action = new
                {
                    value = new
                    {
                        action_id = "approve",
                    },
                },
            },
        });

        var response = await adapter.HandleWebhookAsync(CreateSignedRequest(payload, "encrypt-key"));

        response.StatusCode.ShouldBe(200);
        response.Activity.ShouldBeNull();
    }

    [Fact]
    public async Task HandleWebhookAsync_PlaintextCallbackWithEncryptKeyAndMissingSignature_Returns401()
    {
        var harness = new LarkAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        var payload = JsonSerializer.Serialize(new
        {
            schema = "2.0",
            header = new
            {
                event_type = "im.message.receive_v1",
                token = harness.DefaultBinding.VerificationToken,
            },
            @event = new
            {
                sender = new
                {
                    sender_id = new
                    {
                        open_id = "user-open-1",
                    },
                    sender_type = "user",
                },
                message = new
                {
                    chat_id = "group-1",
                    message_id = "msg-1",
                    message_type = "text",
                    chat_type = "group",
                    content = JsonSerializer.Serialize(new { text = "hello" }),
                },
            },
        });

        var response = await adapter.HandleWebhookAsync(new LarkWebhookRequest(Encoding.UTF8.GetBytes(payload)));

        response.StatusCode.ShouldBe(401);
        response.Activity.ShouldBeNull();
    }

    [Fact]
    public async Task ContinueConversationAsync_OnBehalfOfUser_UsesUserCredential()
    {
        var harness = new LarkAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        var result = await adapter.ContinueConversationAsync(
            ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("conformance-bot"),
                ConversationScope.DirectMessage,
                partition: null,
                "delegate-user"),
            SampleMessageContent.SimpleText("hello"),
            AuthContext.OnBehalfOfUser("vault://users/delegate", "delegate-user"),
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        harness.HttpHandler.LastAuthorization.ShouldBe("user-access-token");
    }

    [Fact]
    public async Task HandleWebhookAsync_WhenRedactorThrows_FailsClosed()
    {
        var credentialProvider = new TestCredentialProvider();
        credentialProvider.Set("vault://bots/test", JsonSerializer.Serialize(new
        {
            access_token = "bot-token",
            encrypt_key = "encrypt-key",
        }));
        var adapter = new LarkChannelAdapter(
            credentialProvider,
            new LarkMessageComposer(),
            new ThrowingRedactor(),
            NullLogger<LarkChannelAdapter>.Instance,
            new HttpClient(new RecordingLarkHttpHandler())
            {
                BaseAddress = LarkChannelDefaults.DefaultBaseAddress,
            });
        var binding = ChannelTransportBinding.Create(
            ChannelBotDescriptor.Create("reg-1", ChannelId.From("lark"), BotInstanceId.From("bot-1")),
            "vault://bots/test",
            "verify-token");
        await adapter.InitializeAsync(binding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        var payload = JsonSerializer.Serialize(new
        {
            schema = "2.0",
            header = new
            {
                event_type = "im.message.receive_v1",
                token = "verify-token",
            },
            @event = new
            {
                sender = new
                {
                    sender_id = new
                    {
                        open_id = "user-1",
                    },
                    sender_type = "user",
                },
                message = new
                {
                    chat_id = "group-1",
                    message_id = "msg-1",
                    message_type = "text",
                    chat_type = "group",
                    content = JsonSerializer.Serialize(new { text = "hello" }),
                },
            },
        });

        var response = await adapter.HandleWebhookAsync(CreateSignedRequest(payload, "encrypt-key"));

        response.StatusCode.ShouldBe(503);
        response.Activity.ShouldBeNull();
    }

    private static LarkWebhookRequest CreateSignedRequest(string payload, string encryptKey)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        const string nonce = "nonce";
        return new LarkWebhookRequest(
            Encoding.UTF8.GetBytes(payload),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["X-Lark-Request-Timestamp"] = timestamp,
                ["X-Lark-Request-Nonce"] = nonce,
                ["X-Lark-Signature"] = LarkChannelAdapter.ComputeLarkSignature(timestamp, nonce, encryptKey, payload),
            });
    }

    private sealed class ThrowingRedactor : IPayloadRedactor
    {
        public Task<RedactionResult> RedactAsync(ChannelId channel, byte[] rawPayload, CancellationToken ct) =>
            throw new InvalidOperationException("redactor failed");

        public Task<HealthStatus> HealthCheckAsync(CancellationToken ct) =>
            Task.FromResult(HealthStatus.Unhealthy);
    }
}
