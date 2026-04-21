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
                ["X-Lark-Request-Timestamp"] = "123",
                ["X-Lark-Request-Nonce"] = "nonce",
                ["X-Lark-Signature"] = LarkChannelAdapter.ComputeLarkSignature("123", "nonce", "encrypt-key", outerPayload),
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
                BaseAddress = new Uri("https://open.feishu.cn", UriKind.Absolute),
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

        var response = await adapter.HandleWebhookAsync(new LarkWebhookRequest(Encoding.UTF8.GetBytes(payload)));

        response.StatusCode.ShouldBe(503);
        response.Activity.ShouldBeNull();
    }

    private sealed class ThrowingRedactor : IPayloadRedactor
    {
        public Task<RedactionResult> RedactAsync(ChannelId channel, byte[] rawPayload, CancellationToken ct) =>
            throw new InvalidOperationException("redactor failed");

        public Task<HealthStatus> HealthCheckAsync(CancellationToken ct) =>
            Task.FromResult(HealthStatus.Unhealthy);
    }
}
