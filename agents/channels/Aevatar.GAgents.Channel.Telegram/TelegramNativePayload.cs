using Aevatar.GAgents.Channel.Abstractions;
using Telegram.Bot.Types.ReplyMarkups;

namespace Aevatar.GAgents.Channel.Telegram;

/// <summary>
/// Composer output for Telegram outbound calls.
/// </summary>
/// <param name="Text">The text or caption rendered to the user.</param>
/// <param name="ReplyMarkup">The optional inline keyboard.</param>
/// <param name="Attachment">The optional attachment that drives <c>sendPhoto</c> or <c>sendDocument</c>.</param>
/// <param name="Capability">The evaluated composition capability.</param>
public sealed record TelegramNativePayload(
    string Text,
    InlineKeyboardMarkup? ReplyMarkup,
    AttachmentRef? Attachment,
    ComposeCapability Capability);
