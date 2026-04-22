namespace Aevatar.AI.ToolProviders.Lark;

public sealed class LarkToolOptions
{
    public string ProviderSlug { get; set; } = "api-lark-bot";
    public bool EnableMessageSend { get; set; } = true;
    public bool EnableChatLookup { get; set; } = true;
}
