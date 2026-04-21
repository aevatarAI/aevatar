using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Aevatar.GAgents.Channel.Telegram;

/// <summary>
/// Default Telegram Bot API client backed by the official <c>Telegram.Bot</c> SDK.
/// </summary>
public sealed class TelegramBotApiClient : ITelegramApiClient
{
    private readonly ITelegramBotClientFactory _clientFactory;
    private static readonly UpdateType[] AllowedUpdates = [UpdateType.Message, UpdateType.ChannelPost, UpdateType.CallbackQuery];

    /// <summary>
    /// Creates the default API client.
    /// </summary>
    public TelegramBotApiClient(ITelegramBotClientFactory clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Update>> GetUpdatesAsync(
        string botToken,
        int? offset,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var client = _clientFactory.Create(botToken);
        var updates = await client.GetUpdates(
            offset,
            timeout: timeoutSeconds,
            allowedUpdates: AllowedUpdates,
            cancellationToken: ct);
        return updates;
    }

    /// <inheritdoc />
    public async Task<TelegramSentActivity> SendMessageAsync(
        string botToken,
        long chatId,
        string text,
        InlineKeyboardMarkup? replyMarkup,
        int? replyToMessageId,
        CancellationToken ct)
    {
        var client = _clientFactory.Create(botToken);
        var message = await client.SendMessage(
            chatId,
            text,
            replyParameters: replyToMessageId.HasValue ? new ReplyParameters { MessageId = replyToMessageId.Value } : null,
            replyMarkup: replyMarkup,
            cancellationToken: ct);
        return new TelegramSentActivity(message.Chat.Id, message.MessageId);
    }

    /// <inheritdoc />
    public async Task<TelegramSentActivity> SendPhotoAsync(
        string botToken,
        long chatId,
        TelegramAttachmentContent attachment,
        string? caption,
        InlineKeyboardMarkup? replyMarkup,
        int? replyToMessageId,
        CancellationToken ct)
    {
        var client = _clientFactory.Create(botToken);
        var message = await client.SendPhoto(
            chatId,
            CreateInputFile(attachment),
            caption: caption,
            parseMode: ParseMode.Html,
            replyParameters: replyToMessageId.HasValue ? new ReplyParameters { MessageId = replyToMessageId.Value } : null,
            replyMarkup: replyMarkup,
            cancellationToken: ct);
        return new TelegramSentActivity(message.Chat.Id, message.MessageId);
    }

    /// <inheritdoc />
    public async Task<TelegramSentActivity> SendDocumentAsync(
        string botToken,
        long chatId,
        TelegramAttachmentContent attachment,
        string? caption,
        InlineKeyboardMarkup? replyMarkup,
        int? replyToMessageId,
        CancellationToken ct)
    {
        var client = _clientFactory.Create(botToken);
        var message = await client.SendDocument(
            chatId,
            CreateInputFile(attachment),
            caption: caption,
            parseMode: ParseMode.Html,
            replyParameters: replyToMessageId.HasValue ? new ReplyParameters { MessageId = replyToMessageId.Value } : null,
            replyMarkup: replyMarkup,
            cancellationToken: ct);
        return new TelegramSentActivity(message.Chat.Id, message.MessageId);
    }

    /// <inheritdoc />
    public async Task<TelegramSentActivity> EditMessageTextAsync(
        string botToken,
        long chatId,
        int messageId,
        string text,
        InlineKeyboardMarkup? replyMarkup,
        CancellationToken ct)
    {
        var client = _clientFactory.Create(botToken);
        var message = await client.EditMessageText(
            chatId,
            messageId,
            text,
            parseMode: ParseMode.Html,
            replyMarkup: replyMarkup,
            cancellationToken: ct);
        return new TelegramSentActivity(chatId, message.MessageId);
    }

    /// <inheritdoc />
    public async Task DeleteMessageAsync(
        string botToken,
        long chatId,
        int messageId,
        CancellationToken ct)
    {
        var client = _clientFactory.Create(botToken);
        await client.DeleteMessage(chatId, messageId, ct);
    }

    private static InputFile CreateInputFile(TelegramAttachmentContent attachment)
    {
        if (!string.IsNullOrWhiteSpace(attachment.TelegramFileId))
            return attachment.TelegramFileId!;
        if (!string.IsNullOrWhiteSpace(attachment.ExternalUrl))
            return attachment.ExternalUrl!;
        if (attachment.Content is null)
            throw new InvalidOperationException("Telegram attachment must provide content, external URL, or file id.");

        return InputFile.FromStream(attachment.Content, attachment.FileName);
    }
}

/// <summary>
/// Creates SDK clients for raw bot tokens.
/// </summary>
public interface ITelegramBotClientFactory
{
    /// <summary>
    /// Creates one SDK client for the supplied bot token.
    /// </summary>
    ITelegramBotClient Create(string botToken);
}

/// <summary>
/// Default client factory backed by <see cref="TelegramBotClient"/>.
/// </summary>
public sealed class TelegramBotClientFactory : ITelegramBotClientFactory
{
    /// <inheritdoc />
    public ITelegramBotClient Create(string botToken) => new TelegramBotClient(botToken);
}
