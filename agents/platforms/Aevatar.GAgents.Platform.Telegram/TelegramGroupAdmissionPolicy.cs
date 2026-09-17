using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.Platform.Telegram;

public sealed class TelegramGroupAdmissionPolicy : IChannelGroupAdmissionPolicy
{
    public string Platform => "telegram";
    public Task<bool> IsAddressedAsync(ChatActivity activity, ChannelBotRegistrationEntry registration,
        ConversationTurnRuntimeContext runtimeContext, CancellationToken ct) =>
        Task.FromResult(activity.Type == ActivityType.Message &&
            activity.Conversation?.Scope is ConversationScope.Group or ConversationScope.Channel);
}
