using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Telegram;
using Aevatar.GAgents.Channel.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Aevatar.GAgents.Channel.Telegram.Tests;

public sealed class TelegramChannelAdapterModeTests
{
    [Fact]
    public async Task StartReceiving_LongPollingMode_PublishesInboundMessage()
    {
        var harness = new TelegramAdapterHarness();
        var adapter = harness.Reset(TransportMode.LongPolling);
        harness.HttpHandler.EnqueuePollResponse(
            TelegramWebhookFixture.BuildPayload(InboundActivitySeed.DirectMessage("hello long polling"), out _));

        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        try
        {
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var activity = await adapter.InboundStream.ReadAsync(readTimeout.Token);

            activity.Content.Text.ShouldBe("hello long polling");
            activity.Conversation.Scope.ShouldBe(ConversationScope.DirectMessage);
        }
        finally
        {
            await adapter.StopReceivingAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HandleWebhookAsync_ShouldDifferentiateSupergroupAndChannelPosts()
    {
        var harness = new TelegramAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        try
        {
            var supergroupPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                update_id = 8001,
                message = new
                {
                    message_id = 501,
                    date = 1_714_000_000,
                    chat = new
                    {
                        id = -100200300400L,
                        type = "supergroup",
                    },
                    from = new
                    {
                        id = 77,
                        is_bot = false,
                        first_name = "Alice",
                    },
                    text = "supergroup message",
                },
            });
            var channelPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                update_id = 8002,
                channel_post = new
                {
                    message_id = 601,
                    date = 1_714_000_010,
                    chat = new
                    {
                        id = -100500600700L,
                        type = "channel",
                        title = "ops-channel",
                    },
                    text = "channel post",
                },
            });

            var supergroupResponse = await adapter.HandleWebhookAsync(new TelegramWebhookRequest(supergroupPayload, SecretHeaders("secret-primary")));
            var channelResponse = await adapter.HandleWebhookAsync(new TelegramWebhookRequest(channelPayload, SecretHeaders("secret-primary")));

            supergroupResponse.StatusCode.ShouldBe(200);
            supergroupResponse.Activity.ShouldNotBeNull();
            supergroupResponse.Activity.Conversation.Scope.ShouldBe(ConversationScope.Group);
            supergroupResponse.Activity.Conversation.CanonicalKey.ShouldContain("supergroup");
            supergroupResponse.Activity.RawPayloadBlobRef.ShouldStartWith("telegram-raw:");

            channelResponse.StatusCode.ShouldBe(200);
            channelResponse.Activity.ShouldNotBeNull();
            channelResponse.Activity.Conversation.Scope.ShouldBe(ConversationScope.Channel);
            channelResponse.Activity.Conversation.CanonicalKey.ShouldContain("channel");
            channelResponse.Activity.RawPayloadBlobRef.ShouldStartWith("telegram-raw:");
        }
        finally
        {
            await adapter.StopReceivingAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HandleWebhookAsync_AttachmentOnlyMessage_PreservesAttachment()
    {
        var harness = new TelegramAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                update_id = 9101,
                message = new
                {
                    message_id = 701,
                    date = 1_714_000_000,
                    chat = new
                    {
                        id = 42,
                        type = "private",
                    },
                    from = new
                    {
                        id = 77,
                        is_bot = false,
                        first_name = "Alice",
                    },
                    photo = new object[]
                    {
                        new
                        {
                            file_id = "photo-small",
                            file_size = 32,
                        },
                        new
                        {
                            file_id = "photo-large",
                            file_size = 128,
                        },
                    },
                },
            });

            var response = await adapter.HandleWebhookAsync(new TelegramWebhookRequest(payload, SecretHeaders("secret-primary")));

            response.StatusCode.ShouldBe(200);
            response.Activity.ShouldNotBeNull();
            response.Activity.Content.Text.ShouldBeEmpty();
            response.Activity.Content.Attachments.Count.ShouldBe(1);
            response.Activity.Content.Attachments[0].AttachmentId.ShouldBe("photo-large");
            response.Activity.Content.Attachments[0].Kind.ShouldBe(AttachmentKind.Image);
        }
        finally
        {
            await adapter.StopReceivingAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HandleWebhookAsync_CallbackQuery_BecomesCardActionSubmission()
    {
        var harness = new TelegramAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                update_id = 9201,
                callback_query = new
                {
                    id = "callback-1",
                    data = "confirm",
                    from = new
                    {
                        id = 91,
                        username = "alice",
                    },
                    message = new
                    {
                        message_id = 333,
                        date = 1_714_000_000,
                        chat = new
                        {
                            id = 42,
                            type = "private",
                        },
                    },
                },
            });

            var response = await adapter.HandleWebhookAsync(new TelegramWebhookRequest(payload, SecretHeaders("secret-primary")));

            response.StatusCode.ShouldBe(200);
            response.Activity.ShouldNotBeNull();
            response.Activity.Type.ShouldBe(ActivityType.CardAction);
            response.Activity.Content.CardAction.ShouldNotBeNull();
            response.Activity.Content.CardAction.ActionId.ShouldBe("confirm");
            response.Activity.Content.CardAction.SourceMessageId.ShouldBe("telegram:message:42:333");
        }
        finally
        {
            await adapter.StopReceivingAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task BeginStreamingReplyAsync_ShouldFinalizeByEditingMessage()
    {
        var harness = new TelegramAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        var reference = ConversationReference.TelegramPrivate(BotInstanceId.From("telegram-primary-bot"), "42");
        try
        {
            await using var handle = await adapter.BeginStreamingReplyAsync(
                reference,
                SampleMessageContent.SimpleText("seed"),
                CancellationToken.None);

            var releaseEdit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.HttpHandler.BlockNextEditCompletion = releaseEdit;
            await handle.AppendAsync(new StreamChunk
            {
                SequenceNumber = 1,
                Delta = " plus",
            });
            await harness.HttpHandler.FirstEditCallAsync.WaitAsync(TimeSpan.FromSeconds(5));

            var completeTask = handle.CompleteAsync(SampleMessageContent.SimpleText("seed plus final"));
            completeTask.IsCompleted.ShouldBeFalse();

            releaseEdit.TrySetResult(true);
            await completeTask;

            harness.HttpHandler.EditCallCount.ShouldBe(2);
            harness.HttpHandler.ReadText(harness.HttpHandler.LastMessageId!).ShouldBe("seed plus final");
        }
        finally
        {
            await adapter.StopReceivingAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartReceiving_LongPollingAuthFailure_StopsRetryLoop()
    {
        var harness = new TelegramAdapterHarness();
        var adapter = harness.Reset(TransportMode.LongPolling);
        harness.HttpHandler.EnqueuePollFailure(401, "unauthorized");

        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        try
        {
            await harness.HttpHandler.FirstPollCallAsync.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await adapter.StopReceivingAsync(CancellationToken.None);
        }

        harness.HttpHandler.PollCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task SendAsync_RefreshesBotCredentialBeforeOutboundRequest()
    {
        var harness = new TelegramAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);
        harness.CredentialProvider.Set("vault://telegram/primary", "rotated-bot-token");

        try
        {
            var emit = await adapter.SendAsync(
                ConversationReference.TelegramPrivate(BotInstanceId.From("telegram-primary-bot"), "42"),
                SampleMessageContent.SimpleText("hello"),
                CancellationToken.None);

            emit.Success.ShouldBeTrue();
            harness.HttpHandler.LastBotToken.ShouldBe("rotated-bot-token");
        }
        finally
        {
            await adapter.StopReceivingAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ContinueConversationAsync_OnBehalfOfUser_ReturnsUnsupported()
    {
        var harness = new TelegramAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        try
        {
            var emit = await adapter.ContinueConversationAsync(
                ConversationReference.TelegramPrivate(BotInstanceId.From("telegram-primary-bot"), "42"),
                SampleMessageContent.SimpleText("hello"),
                AuthContext.OnBehalfOfUser("vault://users/delegate", "delegate-user"),
                CancellationToken.None);

            emit.Success.ShouldBeFalse();
            emit.ErrorCode.ShouldBe("principal_unsupported");
            emit.Capability.ShouldBe(ComposeCapability.Unsupported);
        }
        finally
        {
            await adapter.StopReceivingAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HandleWebhookAsync_WhenRedactorThrows_FailsClosed()
    {
        var credentialProvider = new TestCredentialProvider();
        credentialProvider.Set("vault://telegram/test", "bot-token");
        var adapter = new TelegramChannelAdapter(
            credentialProvider,
            new TelegramMessageComposer(),
            new ThrowingRedactor(),
            NullLogger<TelegramChannelAdapter>.Instance,
            new HttpClient(new RecordingTelegramHttpHandler())
            {
                BaseAddress = TelegramChannelDefaults.DefaultBaseAddress,
            });
        var binding = ChannelTransportBinding.Create(
            ChannelBotDescriptor.Create("telegram-test", ChannelId.From("telegram"), BotInstanceId.From("telegram-test-bot")),
            "vault://telegram/test",
            "secret-primary");
        await adapter.InitializeAsync(binding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        try
        {
            var payload = TelegramWebhookFixture.BuildPayload(InboundActivitySeed.DirectMessage("hello"), out _);

            var response = await adapter.HandleWebhookAsync(new TelegramWebhookRequest(payload, SecretHeaders("secret-primary")));

            response.StatusCode.ShouldBe(503);
            response.Activity.ShouldBeNull();
            response.SanitizedPayload.ShouldBeNull();
        }
        finally
        {
            await adapter.StopReceivingAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SendAsync_WhitespaceOnlyTextWithoutAttachment_ReturnsFailure()
    {
        var harness = new TelegramAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        try
        {
            var emit = await adapter.SendAsync(
                ConversationReference.TelegramPrivate(BotInstanceId.From("telegram-primary-bot"), "42"),
                new MessageContent
                {
                    Text = "   ",
                    Disposition = MessageDisposition.Normal,
                },
                CancellationToken.None);

            emit.Success.ShouldBeFalse();
            emit.ErrorCode.ShouldBe("telegram_empty_message");
        }
        finally
        {
            await adapter.StopReceivingAsync(CancellationToken.None);
        }
    }

    private static IReadOnlyDictionary<string, string> SecretHeaders(string secret) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TelegramChannelDefaults.SecretHeaderName] = secret,
        };
}
