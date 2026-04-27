namespace Aevatar.AI.ToolProviders.Telegram;

public sealed class TelegramToolOptions
{
    public string ProviderSlug { get; set; } = "api-telegram-bot";
    public bool EnableMessageSend { get; set; } = true;
    public bool EnableChatLookup { get; set; } = true;
}
