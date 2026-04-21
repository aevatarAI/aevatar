using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Aevatar.GAgents.Channel.Telegram;

/// <summary>
/// Narrow Telegram Bot API surface used by the adapter.
/// </summary>
public interface ITelegramApiClient
{
    /// <summary>
    /// Pulls updates through Telegram long polling.
    /// </summary>
    Task<IReadOnlyList<Update>> GetUpdatesAsync(
        string botToken,
        int? offset,
        int timeoutSeconds,
        CancellationToken ct);

    /// <summary>
    /// Sends one text message.
    /// </summary>
    Task<TelegramSentActivity> SendMessageAsync(
        string botToken,
        long chatId,
        string text,
        InlineKeyboardMarkup? replyMarkup,
        int? replyToMessageId,
        CancellationToken ct);

    /// <summary>
    /// Sends one photo attachment with optional caption and inline keyboard.
    /// </summary>
    Task<TelegramSentActivity> SendPhotoAsync(
        string botToken,
        long chatId,
        TelegramAttachmentContent attachment,
        string? caption,
        InlineKeyboardMarkup? replyMarkup,
        int? replyToMessageId,
        CancellationToken ct);

    /// <summary>
    /// Sends one document attachment with optional caption and inline keyboard.
    /// </summary>
    Task<TelegramSentActivity> SendDocumentAsync(
        string botToken,
        long chatId,
        TelegramAttachmentContent attachment,
        string? caption,
        InlineKeyboardMarkup? replyMarkup,
        int? replyToMessageId,
        CancellationToken ct);

    /// <summary>
    /// Updates one previously sent text message.
    /// </summary>
    Task<TelegramSentActivity> EditMessageTextAsync(
        string botToken,
        long chatId,
        int messageId,
        string text,
        InlineKeyboardMarkup? replyMarkup,
        CancellationToken ct);

    /// <summary>
    /// Deletes one previously sent message.
    /// </summary>
    Task DeleteMessageAsync(
        string botToken,
        long chatId,
        int messageId,
        CancellationToken ct);
}

/// <summary>
/// Represents one adapter-owned Telegram message identifier.
/// </summary>
/// <param name="ChatId">The Telegram chat id.</param>
/// <param name="MessageId">The Telegram message id.</param>
public readonly record struct TelegramSentActivity(long ChatId, int MessageId);
