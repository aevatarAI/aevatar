using System.Globalization;
using System.Text;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Telegram;

public sealed class TelegramMessageComposer : IMessageComposer<TelegramOutboundMessage>
{
    private const int TelegramTextLimit = 4096;
    private const int TelegramCaptionLimit = 1024;
    private const int CallbackDataMaxBytes = 64;

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

        if (intent.Actions.Count == 0)
        {
            return new TelegramOutboundMessage(
                MessageType: "text",
                ContentJson: JsonSerializer.Serialize(new { text = effectiveText }),
                PlainText: effectiveText,
                IsInteractive: false);
        }

        var supportsButtons = capabilities.SupportsActionButtons;
        var keyboard = supportsButtons ? BuildInlineKeyboard(intent.Actions) : null;
        if (keyboard is null)
        {
            return new TelegramOutboundMessage(
                MessageType: "text",
                ContentJson: JsonSerializer.Serialize(new { text = effectiveText }),
                PlainText: effectiveText,
                IsInteractive: false);
        }

        var contentJson = JsonSerializer.Serialize(new
        {
            text = effectiveText,
            reply_markup = new
            {
                inline_keyboard = keyboard,
            },
        });

        return new TelegramOutboundMessage(
            MessageType: "interactive",
            ContentJson: contentJson,
            PlainText: effectiveText,
            IsInteractive: true);
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

    private static object[][]? BuildInlineKeyboard(IEnumerable<ActionElement> actions)
    {
        var rows = actions
            .Where(static action => action.Kind == ActionElementKind.Button && !string.IsNullOrWhiteSpace(action.Label))
            .Select(static action => new object[]
            {
                new
                {
                    text = action.Label,
                    callback_data = BuildCallbackData(action),
                },
            })
            .ToArray();

        return rows.Length == 0 ? null : rows;
    }

    private static string BuildCallbackData(ActionElement action)
    {
        var raw = !string.IsNullOrWhiteSpace(action.Value)
            ? action.Value
            : !string.IsNullOrWhiteSpace(action.ActionId)
                ? action.ActionId
                : action.Label;
        if (Encoding.UTF8.GetByteCount(raw) <= CallbackDataMaxBytes)
            return raw;

        var textInfo = new StringInfo(raw);
        var builder = new StringBuilder();
        for (var i = 0; i < textInfo.LengthInTextElements; i++)
        {
            var next = builder.ToString() + textInfo.SubstringByTextElements(i, 1);
            if (Encoding.UTF8.GetByteCount(next) > CallbackDataMaxBytes)
                break;

            builder.Append(textInfo.SubstringByTextElements(i, 1));
        }

        return builder.ToString();
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

        var text = builder.ToString().Trim();
        if (maxLength <= 0)
            return text;

        var textInfo = new StringInfo(text);
        if (textInfo.LengthInTextElements <= maxLength)
            return text;

        return textInfo.SubstringByTextElements(0, maxLength);
    }

    private static void AppendParagraph(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (builder.Length > 0)
            builder.AppendLine().AppendLine();
        builder.Append(value.Trim());
    }

    private static int ResolveTextLimit(int configuredMax, int fallback) =>
        configuredMax > 0 ? Math.Min(configuredMax, fallback) : fallback;
}
