namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Telegram-specific conversation helpers that keep canonical keys deterministic across private chats, groups, and channels.
/// </summary>
public sealed partial class ConversationReference
{
    private static readonly ChannelId TelegramChannelId = ChannelId.From("telegram");

    /// <summary>
    /// Creates one Telegram private-chat conversation reference.
    /// </summary>
    public static ConversationReference TelegramPrivate(BotInstanceId bot, string chatId) =>
        Create(
            TelegramChannelId,
            bot,
            ConversationScope.DirectMessage,
            partition: null,
            "private",
            NormalizeTelegramSegment(chatId, nameof(chatId)));

    /// <summary>
    /// Creates one Telegram group or supergroup conversation reference.
    /// </summary>
    public static ConversationReference TelegramGroup(BotInstanceId bot, string chatId, bool isSupergroup = false) =>
        Create(
            TelegramChannelId,
            bot,
            ConversationScope.Group,
            partition: null,
            isSupergroup ? "supergroup" : "group",
            NormalizeTelegramSegment(chatId, nameof(chatId)));

    /// <summary>
    /// Creates one Telegram channel-post conversation reference.
    /// </summary>
    public static ConversationReference TelegramChannel(BotInstanceId bot, string chatId) =>
        Create(
            TelegramChannelId,
            bot,
            ConversationScope.Channel,
            partition: null,
            "channel",
            NormalizeTelegramSegment(chatId, nameof(chatId)));

    private static string NormalizeTelegramSegment(string value, string paramName) =>
        NormalizeSegment(value, paramName);
}
