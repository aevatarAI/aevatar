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
        // NyxID's Telegram relay does not subscribe to callback_query updates today, so the
        // composer degrades action buttons into a plain-text bullet list of labels rather
        // than emitting an inline_keyboard click-back surface that would never round-trip.
        var native = payload.ShouldBeOfType<TelegramOutboundMessage>();
        native.MessageType.ShouldBe("text");
        native.IsInteractive.ShouldBeFalse();
        native.PlainText.ShouldContain("Confirm");
        native.PlainText.ShouldContain("Cancel");
        native.ContentJson.ShouldNotContain("inline_keyboard");
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
    public void Compose_with_button_intent_degrades_buttons_to_plain_text_bullets()
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
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "cancel",
            Label = "Cancel",
        });

        var payload = CreateComposer().Compose(intent, BuildContext());

        payload.MessageType.ShouldBe("text");
        payload.IsInteractive.ShouldBeFalse();
        payload.ContentJson.ShouldNotContain("inline_keyboard");
        payload.ContentJson.ShouldNotContain("callback_data");
        payload.PlainText.ShouldContain("• Confirm");
        payload.PlainText.ShouldContain("• Cancel");
    }

    [Fact]
    public void Evaluate_with_actions_returns_degraded_because_buttons_are_unavailable()
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
        });

        var capability = CreateComposer().Evaluate(intent, BuildContext());
        capability.ShouldBe(ComposeCapability.Degraded);
    }

    [Fact]
    public void Compose_escapes_legacy_markdown_metacharacters_in_text()
    {
        // NyxID's Telegram relay always sends parse_mode="Markdown", so any unescaped _, *, [
        // or backtick in the text would either turn into formatting or surface as a 400
        // "can't parse entities" rejection.
        var payload = CreateComposer().Compose(
            new MessageContent { Text = "use _foo_ or *bar* with [link](x) and `code`" },
            BuildContext());

        payload.PlainText.ShouldBe(@"use \_foo\_ or \*bar\* with \[link](x) and \`code\`");
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

    [Fact]
    public void DefaultCapabilities_does_not_advertise_action_button_support()
    {
        TelegramMessageComposer.DefaultCapabilities.SupportsActionButtons.ShouldBeFalse();
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
