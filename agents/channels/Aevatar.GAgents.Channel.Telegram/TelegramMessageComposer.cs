using System.Text;
using Aevatar.GAgents.Channel.Abstractions;
using Telegram.Bot.Types.ReplyMarkups;

namespace Aevatar.GAgents.Channel.Telegram;

/// <summary>
/// Composes channel-agnostic message intent into Telegram-native text, inline keyboards, and one optional upload target.
/// </summary>
public sealed class TelegramMessageComposer : IMessageComposer<TelegramNativePayload>
{
    private const int TelegramTextLimit = 4096;
    private const int TelegramCaptionLimit = 1024;

    /// <inheritdoc />
    public ChannelId Channel { get; } = ChannelId.From("telegram");

    /// <inheritdoc />
    public TelegramNativePayload Compose(MessageContent intent, ComposeContext context)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);

        var capability = Evaluate(intent, context);
        var attachment = intent.Attachments.Count > 0 ? intent.Attachments[0].Clone() : null;
        var textLimit = attachment is null
            ? ResolveTextLimit(context.Capabilities.MaxMessageLength, TelegramTextLimit)
            : ResolveTextLimit(Math.Min(context.Capabilities.MaxMessageLength, TelegramCaptionLimit), TelegramCaptionLimit);
        var text = BuildRenderedText(intent, textLimit);
        var replyMarkup = intent.Actions.Count == 0
            ? null
            : BuildInlineKeyboard(intent.Actions, context.Capabilities.SupportsActionButtons);

        return new TelegramNativePayload(text, replyMarkup, attachment, capability);
    }

    object IMessageComposer.Compose(MessageContent intent, ComposeContext context) => Compose(intent, context);

    /// <inheritdoc />
    public ComposeCapability Evaluate(MessageContent intent, ComposeContext context)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);

        var degraded = false;

        if (intent.Disposition == MessageDisposition.Ephemeral && !context.Capabilities.SupportsEphemeral)
            degraded = true;
        if (intent.Cards.Count > 0)
            degraded = true;
        if (intent.Actions.Count > 0 && !context.Capabilities.SupportsActionButtons)
            degraded = true;
        if (intent.Attachments.Count > 1)
            degraded = true;

        var attachmentLimit = intent.Attachments.Count > 0 ? TelegramCaptionLimit : TelegramTextLimit;
        var maxLength = ResolveTextLimit(Math.Min(context.Capabilities.MaxMessageLength, attachmentLimit), attachmentLimit);
        if (BuildRenderedText(intent, int.MaxValue).Length > maxLength)
            degraded = true;

        return degraded ? ComposeCapability.Degraded : ComposeCapability.Exact;
    }

    private static InlineKeyboardMarkup? BuildInlineKeyboard(
        IEnumerable<ActionElement> actions,
        bool supportsActionButtons)
    {
        if (!supportsActionButtons)
            return null;

        var rows = actions
            .Where(static action => action.Kind == ActionElementKind.Button && !string.IsNullOrWhiteSpace(action.Label))
            .Select(static action => new[] { new InlineKeyboardButton(action.Label, BuildCallbackData(action)) })
            .ToArray();

        return rows.Length == 0 ? null : new InlineKeyboardMarkup(rows);
    }

    private static string BuildCallbackData(ActionElement action)
    {
        var raw = !string.IsNullOrWhiteSpace(action.Value)
            ? action.Value
            : !string.IsNullOrWhiteSpace(action.ActionId)
                ? action.ActionId
                : action.Label;
        return raw.Length <= 64 ? raw : raw[..64];
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
        if (string.IsNullOrWhiteSpace(text))
            text = "(empty)";
        return text.Length <= maxLength ? text : text[..maxLength];
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
