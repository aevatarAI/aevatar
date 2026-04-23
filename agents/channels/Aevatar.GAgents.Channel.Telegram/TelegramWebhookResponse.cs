using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Telegram;

public sealed record TelegramWebhookResponse(
    int StatusCode,
    string? ResponseBody,
    ChatActivity? Activity,
    byte[]? SanitizedPayload);
