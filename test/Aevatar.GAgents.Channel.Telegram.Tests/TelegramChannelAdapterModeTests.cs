using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Telegram;
using Aevatar.GAgents.Channel.Testing;
using Shouldly;
using global::Telegram.Bot;
using global::Telegram.Bot.Exceptions;
using global::Telegram.Bot.Types;

namespace Aevatar.GAgents.Channel.Telegram.Tests;

public sealed class TelegramChannelAdapterModeTests
{
    [Fact]
    public async Task StartReceiving_LongPollingMode_PublishesInboundMessage()
    {
        var harness = new TelegramAdapterHarness(TransportMode.LongPolling);
        var adapter = harness.Reset(TransportMode.LongPolling);
        var update = JsonSerializer.Deserialize<Update>(
            TelegramWebhookFixture.BuildPayload(InboundActivitySeed.DirectMessage("hello long polling"), out _),
            JsonBotAPI.Options)!;
        harness.Api.EnqueuePollResponse(update);

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
    public async Task AcceptWebhookAsync_ShouldDifferentiateSupergroupAndChannelPosts()
    {
        var harness = new TelegramAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        try
        {
            var supergroupUpdate = TelegramWebhookFixture.BuildUpdate(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("update_id", 8001);
                writer.WritePropertyName("message");
                writer.WriteStartObject();
                writer.WriteNumber("message_id", 501);
                writer.WriteNumber("date", 1_714_000_000);
                writer.WritePropertyName("chat");
                writer.WriteStartObject();
                writer.WriteNumber("id", -100200300400L);
                writer.WriteString("type", "supergroup");
                writer.WriteEndObject();
                writer.WritePropertyName("from");
                writer.WriteStartObject();
                writer.WriteNumber("id", 77);
                writer.WriteBoolean("is_bot", false);
                writer.WriteString("first_name", "Alice");
                writer.WriteEndObject();
                writer.WriteString("text", "supergroup message");
                writer.WriteEndObject();
                writer.WriteEndObject();
            });
            var channelUpdate = TelegramWebhookFixture.BuildUpdate(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("update_id", 8002);
                writer.WritePropertyName("channel_post");
                writer.WriteStartObject();
                writer.WriteNumber("message_id", 601);
                writer.WriteNumber("date", 1_714_000_010);
                writer.WritePropertyName("chat");
                writer.WriteStartObject();
                writer.WriteNumber("id", -100500600700L);
                writer.WriteString("type", "channel");
                writer.WriteString("title", "ops-channel");
                writer.WriteEndObject();
                writer.WriteString("text", "channel post");
                writer.WriteEndObject();
                writer.WriteEndObject();
            });

            await using var supergroupStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(supergroupUpdate, JsonBotAPI.Options));
            await using var channelStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(channelUpdate, JsonBotAPI.Options));
            var supergroupActivity = await adapter.AcceptWebhookAsync(supergroupStream, secretToken: "secret-primary");
            var channelActivity = await adapter.AcceptWebhookAsync(channelStream, secretToken: "secret-primary");

            supergroupActivity.ShouldNotBeNull();
            supergroupActivity.Conversation.Scope.ShouldBe(ConversationScope.Group);
            supergroupActivity.Conversation.CanonicalKey.ShouldContain("supergroup");
            supergroupActivity.RawPayloadBlobRef.ShouldBeEmpty();
            channelActivity.ShouldNotBeNull();
            channelActivity.Conversation.Scope.ShouldBe(ConversationScope.Channel);
            channelActivity.Conversation.CanonicalKey.ShouldContain("channel");
            channelActivity.RawPayloadBlobRef.ShouldBeEmpty();
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
            harness.Api.BlockNextEditCompletion = releaseEdit;
            await handle.AppendAsync(new StreamChunk
            {
                SequenceNumber = 1,
                Delta = " plus",
            });
            await harness.Api.FirstEditCallAsync.WaitAsync(TimeSpan.FromSeconds(5));

            var completeTask = handle.CompleteAsync(SampleMessageContent.SimpleText("seed plus final"));
            completeTask.IsCompleted.ShouldBeFalse();

            releaseEdit.TrySetResult(true);
            await completeTask;

            harness.Api.SendCalls.Count(call => call.Kind == "edit").ShouldBe(2);
            harness.Api.SendCalls[^1].Kind.ShouldBe("edit");
            harness.Api.SendCalls[^1].Text.ShouldBe("seed plus final");
        }
        finally
        {
            await adapter.StopReceivingAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartReceiving_LongPollingAuthFailure_StopsRetryLoop()
    {
        var harness = new TelegramAdapterHarness(TransportMode.LongPolling);
        var adapter = harness.Reset(TransportMode.LongPolling);
        harness.Api.EnqueuePollException(new ApiRequestException("unauthorized", 401));

        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        try
        {
            await harness.Api.FirstPollCallAsync.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await adapter.StopReceivingAsync(CancellationToken.None);
        }

        harness.Api.PollCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task SendAsync_WhitespaceOnlyTextWithoutAttachment_ReturnsFailure()
    {
        var harness = new TelegramAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        var reference = ConversationReference.TelegramPrivate(BotInstanceId.From("telegram-primary-bot"), "42");
        try
        {
            var emit = await adapter.SendAsync(reference, new MessageContent
            {
                Text = "   ",
                Disposition = MessageDisposition.Normal,
            }, CancellationToken.None);

            emit.Success.ShouldBeFalse();
            emit.ErrorCode.ShouldBe("telegram_empty_message");
            harness.Api.SendCalls.ShouldBeEmpty();
        }
        finally
        {
            await adapter.StopReceivingAsync(CancellationToken.None);
        }
    }
}
