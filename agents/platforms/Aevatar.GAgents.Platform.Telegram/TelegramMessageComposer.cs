using System.Globalization;
using System.Text;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Telegram;

public sealed class TelegramMessageComposer : IMessageComposer<TelegramOutboundMessage>
{
    private const int TelegramTextLimit = 4096;
    private const int TelegramCaptionLimit = 1024;

    public static readonly ChannelCapabilities DefaultCapabilities = new()
    {
        SupportsEphemeral = false,
        SupportsEdit = true,
        SupportsDelete = true,
        SupportsThread = false,
        Streaming = StreamingSupport.EditLoopRateLimited,
        SupportsFiles = false,
        MaxMessageLength = TelegramTextLimit,
        SupportsActionButtons = true,
        SupportsConfirmDialog = false,
        SupportsModal = false,
        SupportsMention = false,
        SupportsTyping = false,
        SupportsReactions = false,
        RecommendedStreamDebounceMs = 3000,
        Transport = TransportMode.Webhook,
    };

    public ChannelId Channel { get; } = ChannelId.From("telegram");

    public TelegramOutboundMessage Compose(MessageContent intent, ComposeContext context)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);

        var capabilities = context.Capabilities ?? DefaultCapabilities;
        var maxLength = ResolveTextLimit(capabilities.MaxMessageLength, TelegramTextLimit);
        var effectiveText = BuildRenderedText(intent, maxLength);
        var buttonActions = GetButtonActions(intent);
        if (buttonActions.Length > 0 && capabilities.SupportsActionButtons)
        {
            var inlineKeyboard = buttonActions
                .Select(action => new[]
                {
                    new
                    {
                        text = action.Label.Trim(),
                        callback_data = BuildCallbackData(action),
                    },
                })
                .ToArray();
            var contentJson = JsonSerializer.Serialize(new
            {
                text = effectiveText,
                reply_markup = new
                {
                    inline_keyboard = inlineKeyboard,
                },
            });
            return new TelegramOutboundMessage(
                MessageType: "text",
                ContentJson: contentJson,
                PlainText: effectiveText,
                IsInteractive: true);
        }

        return new TelegramOutboundMessage(
            MessageType: "text",
            ContentJson: JsonSerializer.Serialize(new { text = effectiveText }),
            PlainText: effectiveText,
            IsInteractive: false);
    }

    object IMessageComposer.Compose(MessageContent intent, ComposeContext context) => Compose(intent, context);

    public ComposeCapability Evaluate(MessageContent intent, ComposeContext context)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);

        var degraded = false;
        var capabilities = context.Capabilities ?? DefaultCapabilities;

        if (intent.Disposition == MessageDisposition.Ephemeral && !capabilities.SupportsEphemeral)
            degraded = true;
        if (intent.Attachments.Count > 0 && !capabilities.SupportsFiles)
            return ComposeCapability.Unsupported;
        if (intent.Actions.Count > 0 && !capabilities.SupportsActionButtons)
            degraded = true;

        var maxLength = ResolveTextLimit(capabilities.MaxMessageLength, TelegramTextLimit);
        if (BuildRenderedText(intent, int.MaxValue).Length > maxLength)
            degraded = true;

        return degraded ? ComposeCapability.Degraded : ComposeCapability.Exact;
    }

    private static string BuildRenderedText(MessageContent intent, int maxLength)
    {
        var builder = new StringBuilder();
        AppendParagraph(builder, intent.Text);

        foreach (var card in intent.Cards)
        {
            AppendParagraph(builder, card.Title);
            AppendParagraph(builder, card.Text);
            foreach (var field in card.Fields)
                AppendParagraph(builder, $"{field.Title}: {field.Text}");
        }

        // Render available action labels as a bullet list so the user can still see what
        // the agent intended to offer, even though clicks cannot round-trip through the
        // current Nyx Telegram relay contract.
        var buttonActions = intent.Actions
            .Where(static action => action.Kind == ActionElementKind.Button && !string.IsNullOrWhiteSpace(action.Label))
            .Select(static action => $"• {action.Label.Trim()}")
            .ToArray();
        if (buttonActions.Length > 0)
        {
            if (builder.Length > 0)
                builder.AppendLine().AppendLine();
            builder.Append(string.Join("\n", buttonActions));
        }

        // NyxID's Telegram relay sends every reply with parse_mode="Markdown"
        // (telegram.rs::send_reply). Escape Telegram's legacy-Markdown control characters so
        // ordinary model output containing _ * [ ` does not turn into half-formatted text or,
        // worse, a "can't parse entities" 400 that breaks the entire reply.
        var escaped = EscapeLegacyMarkdown(builder.ToString().Trim());
        if (maxLength <= 0)
            return escaped;

        var textInfo = new StringInfo(escaped);
        if (textInfo.LengthInTextElements <= maxLength)
            return escaped;

        return textInfo.SubstringByTextElements(0, maxLength);
    }

    private static ActionElement[] GetButtonActions(MessageContent intent) =>
        intent.Actions
            .Where(static action => action.Kind == ActionElementKind.Button && !string.IsNullOrWhiteSpace(action.Label))
            .ToArray();

    private static string BuildCallbackData(ActionElement action)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["action_id"] = action.ActionId,
            ["submitted_value"] = action.Value,
        };
        if (action.Arguments.Count > 0)
        {
            payload["value"] = action.Arguments.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
        }

        return JsonSerializer.Serialize(payload);
    }

    private static void AppendParagraph(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (builder.Length > 0)
            builder.AppendLine().AppendLine();
        builder.Append(value.Trim());
    }

    /// <summary>
    /// Escapes the four characters Telegram's legacy <c>parse_mode=Markdown</c> uses as control
    /// tokens (<c>_</c>, <c>*</c>, <c>[</c>, <c>`</c>) by prefixing each with a backslash so
    /// arbitrary model output never accidentally enters bold / italic / link / code mode and
    /// never trips the Bot API's <c>can't parse entities</c> rejection.
    /// </summary>
    /// <remarks>
    /// MarkdownV2 would require escaping a much larger set, but NyxID's relay sends
    /// <c>parse_mode=Markdown</c> (legacy), so only this minimal set is needed and the escape
    /// stays human-readable.
    /// </remarks>
    private static string EscapeLegacyMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '_' or '*' or '[' or '`')
                builder.Append('\\');
            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static int ResolveTextLimit(int configuredMax, int fallback) =>
        configuredMax > 0 ? Math.Min(configuredMax, fallback) : fallback;
}
