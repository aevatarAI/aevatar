using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Lark;

public sealed record LarkOutboundMessage(
    string MessageType,
    string ContentJson,
    string PlainText,
    bool IsInteractive) : IPlainTextComposedMessage;
