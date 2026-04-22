namespace Aevatar.GAgents.Channel.Lark;

public sealed record LarkWebhookRequest(
    byte[] Body,
    IReadOnlyDictionary<string, string>? Headers = null);
