namespace Aevatar.GAgents.Channel.Telegram;

internal static class TelegramChannelDefaults
{
    public const string HttpClientName = "Aevatar.GAgents.Channel.Telegram";

    public const string SecretHeaderName = "X-Telegram-Bot-Api-Secret-Token";

    public static readonly Uri DefaultBaseAddress = new("https://api.telegram.org", UriKind.Absolute);

    public static readonly string[] AllowedUpdateTypes =
    [
        "message",
        "edited_message",
        "channel_post",
        "edited_channel_post",
        "callback_query",
    ];
}
