namespace Aevatar.GAgents.Channel.Telegram;

public sealed record TelegramWebhookRequest(
    byte[] Body,
    IReadOnlyDictionary<string, string>? Headers = null);
