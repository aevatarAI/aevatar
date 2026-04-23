using System.Globalization;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Lark;

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

        var headerTitle = ResolveHeaderTitle(intent, effectiveText);
        var template = ResolveHeaderTemplate(intent);
        var formMode = RequiresFormWrapping(intent);

        if (formMode)
        {
            var formElements = new List<object>();
            var leading = BuildLeadingMarkdown(effectiveText, intent);
            if (leading is not null)
                formElements.Add(leading);

            formElements.Add(new
            {
                tag = "form",
                name = DefaultFormName,
                elements = intent.Actions.Select(BuildFormChildAction).ToArray(),
            });

            var formCardJson = JsonSerializer.Serialize(new
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
                        content = headerTitle,
                    },
                    template,
                },
                body = new
                {
                    elements = formElements,
                },
            });

            return new LarkOutboundMessage(
                MessageType: "interactive",
                ContentJson: formCardJson,
                PlainText: effectiveText,
                IsInteractive: true);
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
                    content = headerTitle,
                },
                template,
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

    private const string DefaultFormName = "card_form";

    private static bool RequiresFormWrapping(MessageContent intent) =>
        intent.Actions.Any(a => a.Kind == ActionElementKind.TextInput);

    private static string ResolveHeaderTitle(MessageContent intent, string effectiveText)
    {
        if (intent.Cards.Count > 0)
        {
            var first = intent.Cards[0];
            if (!string.IsNullOrWhiteSpace(first.Title))
                return first.Title;
        }

        return string.IsNullOrWhiteSpace(effectiveText) ? "Aevatar" : effectiveText;
    }

    private static string ResolveHeaderTemplate(MessageContent intent) =>
        intent.Actions.Any(a => a.IsDanger) ? "orange" : "blue";

    private static object? BuildLeadingMarkdown(string effectiveText, MessageContent intent)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(effectiveText))
            parts.Add(effectiveText);

        for (var i = 0; i < intent.Cards.Count; i++)
        {
            var card = intent.Cards[i];
            // In form mode the first card's title is consumed as the card header title,
            // so skip the title when rendering its body markdown to avoid duplication.
            var skipTitle = i == 0;
            var markdown = BuildCardMarkdown(card, skipTitle);
            if (!string.IsNullOrWhiteSpace(markdown))
                parts.Add(markdown);
        }

        if (parts.Count == 0)
            return null;

        return new
        {
            tag = "markdown",
            content = string.Join("\n\n", parts),
        };
    }

    private static object BuildFormChildAction(ActionElement action) =>
        action.Kind == ActionElementKind.TextInput
            ? BuildFormInput(action)
            : BuildFormButton(action);

    private static object BuildFormInput(ActionElement action) => new
    {
        tag = "input",
        name = action.ActionId,
        label = new
        {
            tag = "plain_text",
            content = string.IsNullOrWhiteSpace(action.Label) ? action.ActionId : action.Label,
        },
        placeholder = new
        {
            tag = "plain_text",
            content = action.Placeholder ?? string.Empty,
        },
    };

    private static object BuildFormButton(ActionElement action) => new
    {
        tag = "button",
        type = action.IsPrimary ? "primary" : "default",
        name = action.ActionId,
        form_action_type = "submit",
        text = new
        {
            tag = "plain_text",
            content = string.IsNullOrWhiteSpace(action.Label) ? action.ActionId : action.Label,
        },
        value = BuildActionValueObject(action),
    };

    private static object BuildAction(ActionElement action) => new
    {
        tag = "button",
        text = new
        {
            tag = "plain_text",
            content = string.IsNullOrWhiteSpace(action.Label) ? action.ActionId : action.Label,
        },
        type = action.IsPrimary ? "primary" : "default",
        value = BuildActionValueObject(action),
    };

    private static IDictionary<string, object?> BuildActionValueObject(ActionElement action)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["action_id"] = action.ActionId,
            ["value"] = action.Value,
        };

        foreach (var argument in action.Arguments)
        {
            if (string.Equals(argument.Key, "action_id", StringComparison.Ordinal) ||
                string.Equals(argument.Key, "value", StringComparison.Ordinal))
                continue;

            map[argument.Key] = CoerceArgumentValue(argument.Value);
        }

        return map;
    }

    private static object? CoerceArgumentValue(string raw)
    {
        if (bool.TryParse(raw, out var boolean))
            return boolean;
        if (long.TryParse(raw, out var integer))
            return integer;
        return raw;
    }

    private static string BuildCardMarkdown(CardBlock card, bool skipTitle = false)
    {
        var parts = new List<string>();
        if (!skipTitle && !string.IsNullOrWhiteSpace(card.Title))
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
