using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Telegram;

public sealed record TelegramOutboundMessage(
    string MessageType,
    string ContentJson,
    string PlainText,
    bool IsInteractive) : IPlainTextComposedMessage;
