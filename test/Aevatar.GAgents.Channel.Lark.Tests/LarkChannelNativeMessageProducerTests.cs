using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Lark;
using Shouldly;

namespace Aevatar.GAgents.Channel.Lark.Tests;

public sealed class LarkChannelNativeMessageProducerTests
{
    [Fact]
    public void Produce_for_text_only_intent_returns_text_native()
    {
        var producer = new LarkChannelNativeMessageProducer(new LarkMessageComposer());
        var native = producer.Produce(
            new MessageContent { Text = "hello" },
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
            });

        native.Text.ShouldBe("hello");
        native.CardPayload.ShouldBeNull();
        native.MessageType.ShouldBe("text");
        native.IsInteractive.ShouldBeFalse();
    }

    [Fact]
    public void Produce_for_action_intent_returns_interactive_card_payload()
    {
        var producer = new LarkChannelNativeMessageProducer(new LarkMessageComposer());
        var intent = new MessageContent
        {
            Text = "Choose",
        };
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "confirm",
            Label = "Confirm",
            IsPrimary = true,
        });

        var native = producer.Produce(
            intent,
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
            });

        native.IsInteractive.ShouldBeTrue();
        native.CardPayload.ShouldNotBeNull();
        native.MessageType.ShouldBe("interactive");
        native.CardPayload.ShouldBeOfType<JsonElement>();
        var cardJson = JsonSerializer.Serialize(native.CardPayload);
        cardJson.ShouldContain("Confirm");
    }

    [Fact]
    public void Channel_is_lark()
    {
        var producer = new LarkChannelNativeMessageProducer(new LarkMessageComposer());
        producer.Channel.Value.ShouldBe("lark");
    }
}
