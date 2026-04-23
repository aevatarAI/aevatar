namespace Aevatar.GAgents.Channel.Lark;

public sealed record LarkOutboundMessage(
    string MessageType,
    string ContentJson,
    string PlainText,
    bool IsInteractive);
