namespace Aevatar.GAgents.Platform.Lark;

public sealed record LarkWebhookRequest(
    byte[] Body,
    IReadOnlyDictionary<string, string>? Headers = null);
