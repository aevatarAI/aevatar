using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Lark;

public sealed record LarkWebhookResponse(
    int StatusCode,
    string? ResponseBody,
    ChatActivity? Activity,
    byte[]? SanitizedPayload);
