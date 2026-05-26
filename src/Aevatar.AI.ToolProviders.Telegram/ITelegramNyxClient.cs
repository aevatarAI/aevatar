namespace Aevatar.AI.ToolProviders.Telegram;

public interface ITelegramNyxClient
{
    Task<string> SendMessageAsync(string token, TelegramSendMessageRequest request, CancellationToken ct);
    Task<string> GetChatAsync(string token, TelegramGetChatRequest request, CancellationToken ct);
}

public sealed record TelegramSendMessageRequest(
    string ChatId,
    string Text,
    string? ParseMode = null,
    bool? DisableNotification = null,
    int? ReplyToMessageId = null,
    string? ReplyMarkupJson = null);

public sealed record TelegramGetChatRequest(
    string ChatId);
