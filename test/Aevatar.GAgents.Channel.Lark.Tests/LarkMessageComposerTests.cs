using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Lark;
using Aevatar.GAgents.Channel.Testing;
using Shouldly;

namespace Aevatar.GAgents.Channel.Lark.Tests;

public sealed class LarkMessageComposerTests : MessageComposerUnitTests<LarkMessageComposer>
{
    protected override LarkMessageComposer CreateComposer() => new();

    protected override ChannelCapabilities CreateCapabilities() => LarkMessageComposer.DefaultCapabilities.Clone();

    protected override void AssertSimpleTextPayload(object payload, MessageContent intent, ComposeContext context)
    {
        var native = payload.ShouldBeOfType<LarkOutboundMessage>();
        native.MessageType.ShouldBe("text");
        using var document = JsonDocument.Parse(native.ContentJson);
        document.RootElement.GetProperty("text").GetString().ShouldBe(intent.Text);
    }

    protected override void AssertActionsPayload(object payload, MessageContent intent, ComposeContext context, ComposeCapability capability)
    {
        var native = payload.ShouldBeOfType<LarkOutboundMessage>();
        native.MessageType.ShouldBe("interactive");
        native.ContentJson.ShouldContain("Confirm");
        native.ContentJson.ShouldContain("Cancel");
    }

    protected override void AssertCardPayload(object payload, MessageContent intent, ComposeContext context, ComposeCapability capability)
    {
        var native = payload.ShouldBeOfType<LarkOutboundMessage>();
        native.MessageType.ShouldBe("interactive");
        native.ContentJson.ShouldContain("Hero");
        native.ContentJson.ShouldContain("Hero body");
    }

    protected override void AssertOverflowTruncation(object payload, int maxLength)
    {
        var native = payload.ShouldBeOfType<LarkOutboundMessage>();
        native.PlainText.Length.ShouldBeLessThanOrEqualTo(maxLength);
    }

    [Fact]
    public void Compose_WhenTextContainsSurrogatePair_DoesNotSplitTextElement()
    {
        var payload = CreateComposer().Compose(
            new MessageContent
            {
                Text = "A🙂B",
            },
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = new ChannelCapabilities
                {
                    MaxMessageLength = 2,
                },
            });

        payload.PlainText.ShouldBe("A🙂");
    }
}
