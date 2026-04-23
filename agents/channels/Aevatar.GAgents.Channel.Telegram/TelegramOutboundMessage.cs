using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Telegram;

public sealed record TelegramOutboundMessage(
    string Text,
    string? ReplyMarkupJson,
    AttachmentRef? Attachment,
    ComposeCapability Capability);
