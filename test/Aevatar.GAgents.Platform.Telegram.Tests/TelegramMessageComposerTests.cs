using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Testing;
using Aevatar.GAgents.Platform.Telegram;
using Shouldly;

namespace Aevatar.GAgents.Platform.Telegram.Tests;

public sealed class TelegramMessageComposerTests : MessageComposerUnitTests<TelegramMessageComposer>
{
    protected override TelegramMessageComposer CreateComposer() => new();

    protected override ChannelCapabilities CreateCapabilities() => TelegramMessageComposer.DefaultCapabilities.Clone();

    protected override void AssertSimpleTextPayload(object payload, MessageContent intent, ComposeContext context)
    {
        var native = payload.ShouldBeOfType<TelegramOutboundMessage>();
        native.MessageType.ShouldBe("text");
        native.IsInteractive.ShouldBeFalse();
        using var document = JsonDocument.Parse(native.ContentJson);
        document.RootElement.GetProperty("text").GetString().ShouldBe(intent.Text);
    }

    protected override void AssertActionsPayload(object payload, MessageContent intent, ComposeContext context, ComposeCapability capability)
    {
        var native = payload.ShouldBeOfType<TelegramOutboundMessage>();
        if (capability == ComposeCapability.Degraded && !context.Capabilities!.SupportsActionButtons)
        {
            native.MessageType.ShouldBe("text");
            native.IsInteractive.ShouldBeFalse();
            return;
        }

        native.MessageType.ShouldBe("interactive");
        native.IsInteractive.ShouldBeTrue();
        native.ContentJson.ShouldContain("Confirm");
        native.ContentJson.ShouldContain("Cancel");
        native.ContentJson.ShouldContain("inline_keyboard");
    }

    protected override void AssertCardPayload(object payload, MessageContent intent, ComposeContext context, ComposeCapability capability)
    {
        var native = payload.ShouldBeOfType<TelegramOutboundMessage>();
        // Telegram has no native card UI; cards degrade into the rendered text body.
        native.PlainText.ShouldContain("Hero");
        native.PlainText.ShouldContain("Hero body");
    }

    protected override void AssertOverflowTruncation(object payload, int maxLength)
    {
        var native = payload.ShouldBeOfType<TelegramOutboundMessage>();
        native.PlainText.Length.ShouldBeLessThanOrEqualTo(maxLength);
    }

    [Fact]
    public void Compose_text_only_intent_emits_text_message_type()
    {
        var payload = CreateComposer().Compose(
            new MessageContent { Text = "hello" },
            BuildContext());

        payload.MessageType.ShouldBe("text");
        payload.IsInteractive.ShouldBeFalse();
        using var document = JsonDocument.Parse(payload.ContentJson);
        document.RootElement.GetProperty("text").GetString().ShouldBe("hello");
    }

    [Fact]
    public void Compose_with_button_intent_emits_inline_keyboard_payload()
    {
        var intent = new MessageContent
        {
            Text = "Choose",
        };
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "confirm",
            Label = "Confirm",
            Value = "yes",
        });

        var payload = CreateComposer().Compose(intent, BuildContext());

        payload.MessageType.ShouldBe("interactive");
        payload.IsInteractive.ShouldBeTrue();
        using var document = JsonDocument.Parse(payload.ContentJson);
        var rows = document.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard");
        rows.GetArrayLength().ShouldBe(1);
        var firstButton = rows[0][0];
        firstButton.GetProperty("text").GetString().ShouldBe("Confirm");
        firstButton.GetProperty("callback_data").GetString().ShouldBe("yes");
    }

    [Fact]
    public void Compose_with_button_callback_data_truncates_to_telegram_limit()
    {
        var intent = new MessageContent();
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "long-action",
            Label = "Long",
            Value = new string('x', 200),
        });

        var payload = CreateComposer().Compose(intent, BuildContext());
        using var document = JsonDocument.Parse(payload.ContentJson);
        var data = document.RootElement
            .GetProperty("reply_markup")
            .GetProperty("inline_keyboard")[0][0]
            .GetProperty("callback_data")
            .GetString();

        data.ShouldNotBeNull();
        System.Text.Encoding.UTF8.GetByteCount(data!).ShouldBeLessThanOrEqualTo(64);
    }

    [Fact]
    public void Evaluate_attachments_without_files_capability_returns_unsupported()
    {
        var intent = new MessageContent
        {
            Text = "with file",
        };
        intent.Attachments.Add(new AttachmentRef
        {
            ContentType = "image/png",
            ExternalUrl = "https://example.com/cat.png",
        });

        var capability = CreateComposer().Evaluate(intent, BuildContext());
        capability.ShouldBe(ComposeCapability.Unsupported);
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
