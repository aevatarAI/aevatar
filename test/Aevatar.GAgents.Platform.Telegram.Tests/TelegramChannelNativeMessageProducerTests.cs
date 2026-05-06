using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Telegram;
using Shouldly;

namespace Aevatar.GAgents.Platform.Telegram.Tests;

public sealed class TelegramChannelNativeMessageProducerTests
{
    [Fact]
    public void Produce_for_text_only_intent_returns_text_native()
    {
        var producer = new TelegramChannelNativeMessageProducer(new TelegramMessageComposer());
        var native = producer.Produce(
            new MessageContent { Text = "hello" },
            BuildContext());

        native.Text.ShouldBe("hello");
        native.CardPayload.ShouldBeNull();
        native.MessageType.ShouldBe("text");
        native.IsInteractive.ShouldBeFalse();
    }

    [Fact]
    public void Produce_for_button_intent_returns_inline_keyboard_native()
    {
        var producer = new TelegramChannelNativeMessageProducer(new TelegramMessageComposer());
        var intent = new MessageContent { Text = "Choose" };
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "confirm",
            Label = "Confirm",
            Value = "yes",
        });

        var native = producer.Produce(intent, BuildContext());

        native.IsInteractive.ShouldBeTrue();
        native.CardPayload.ShouldNotBeNull();
        native.MessageType.ShouldBe("text");
        native.Text.ShouldNotBeNull();
        native.Text!.ShouldContain("Choose");
        native.Capability.ShouldBe(ComposeCapability.Exact);

        var payload = native.CardPayload.ShouldBeOfType<JsonElement>();
        payload.GetProperty("reply_markup")
            .GetProperty("inline_keyboard")[0][0]
            .GetProperty("text")
            .GetString()
            .ShouldBe("Confirm");
    }

    [Fact]
    public void Channel_is_telegram()
    {
        var producer = new TelegramChannelNativeMessageProducer(new TelegramMessageComposer());
        producer.Channel.Value.ShouldBe("telegram");
    }

    private static ComposeContext BuildContext() => new()
    {
        Conversation = ConversationReference.Create(
            ChannelId.From("telegram"),
            BotInstanceId.From("bot"),
            ConversationScope.DirectMessage,
            partition: null,
            "user-1"),
        Capabilities = TelegramMessageComposer.DefaultCapabilities.Clone(),
    };
}
