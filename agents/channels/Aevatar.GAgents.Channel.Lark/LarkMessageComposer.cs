using System.Globalization;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Lark;

public sealed class LarkMessageComposer : IMessageComposer<LarkOutboundMessage>
{
    public static readonly ChannelCapabilities DefaultCapabilities = new()
    {
        SupportsEphemeral = false,
        SupportsEdit = true,
        SupportsDelete = true,
        SupportsThread = true,
        Streaming = StreamingSupport.Native,
        SupportsFiles = false,
        MaxMessageLength = 2000,
        SupportsActionButtons = true,
        SupportsConfirmDialog = false,
        SupportsModal = false,
        SupportsMention = true,
        SupportsTyping = false,
        SupportsReactions = false,
        RecommendedStreamDebounceMs = 300,
        Transport = TransportMode.Webhook,
    };

    public ChannelId Channel { get; } = ChannelId.From("lark");

    public LarkOutboundMessage Compose(MessageContent intent, ComposeContext context)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);

        var effectiveText = Truncate(intent.Text, context.Capabilities?.MaxMessageLength ?? DefaultCapabilities.MaxMessageLength);
        if (intent.Actions.Count == 0 && intent.Cards.Count == 0)
        {
            return new LarkOutboundMessage(
                MessageType: "text",
                ContentJson: JsonSerializer.Serialize(new { text = effectiveText }),
                PlainText: effectiveText,
                IsInteractive: false);
        }

        var elements = new List<object>();
        if (!string.IsNullOrWhiteSpace(effectiveText))
        {
            elements.Add(new
            {
                tag = "markdown",
                content = effectiveText,
            });
        }

        foreach (var card in intent.Cards)
        {
            elements.Add(new
            {
                tag = "markdown",
                content = BuildCardMarkdown(card),
            });
        }

        if (intent.Actions.Count > 0)
        {
            elements.Add(new
            {
                tag = "action",
                actions = intent.Actions.Select(BuildAction).ToArray(),
            });
        }

        var cardJson = JsonSerializer.Serialize(new
        {
            schema = "2.0",
            config = new
            {
                wide_screen_mode = true,
            },
            header = new
            {
                title = new
                {
                    tag = "plain_text",
                    content = string.IsNullOrWhiteSpace(effectiveText) ? "Aevatar" : effectiveText,
                },
                template = "blue",
            },
            elements,
        });

        return new LarkOutboundMessage(
            MessageType: "interactive",
            ContentJson: cardJson,
            PlainText: effectiveText,
            IsInteractive: true);
    }

    object IMessageComposer.Compose(MessageContent intent, ComposeContext context) => Compose(intent, context);

    public ComposeCapability Evaluate(MessageContent intent, ComposeContext context)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);

        if (intent.Disposition == MessageDisposition.Ephemeral)
            return ComposeCapability.Degraded;

        if (intent.Attachments.Count > 0 && !(context.Capabilities?.SupportsFiles ?? DefaultCapabilities.SupportsFiles))
            return ComposeCapability.Unsupported;

        if (intent.Actions.Count > 0 && !(context.Capabilities?.SupportsActionButtons ?? DefaultCapabilities.SupportsActionButtons))
            return ComposeCapability.Degraded;

        return ComposeCapability.Exact;
    }

    private static object BuildAction(ActionElement action) => new
    {
        tag = "button",
        text = new
        {
            tag = "plain_text",
            content = string.IsNullOrWhiteSpace(action.Label) ? action.ActionId : action.Label,
        },
        type = action.IsPrimary ? "primary" : "default",
        value = new
        {
            action_id = action.ActionId,
            value = action.Value,
        },
    };

    private static string BuildCardMarkdown(CardBlock card)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(card.Title))
            parts.Add($"**{card.Title}**");
        if (!string.IsNullOrWhiteSpace(card.Text))
            parts.Add(card.Text);

        foreach (var field in card.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Title) && string.IsNullOrWhiteSpace(field.Text))
                continue;

            parts.Add($"- {field.Title}: {field.Text}".Trim());
        }

        return string.Join("\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string Truncate(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        if (maxLength <= 0)
            return text;

        var textInfo = new StringInfo(text);
        if (textInfo.LengthInTextElements <= maxLength)
            return text;

        return textInfo.SubstringByTextElements(0, maxLength);
    }
}
