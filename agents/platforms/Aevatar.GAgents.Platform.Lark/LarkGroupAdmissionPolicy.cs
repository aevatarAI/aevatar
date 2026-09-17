using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.Platform.Lark;

public class LarkGroupAdmissionPolicy(ILarkBotIdentityResolver? identityResolver = null) : IChannelGroupAdmissionPolicy
{
    public virtual string Platform => "lark";
    public async Task<bool> IsAddressedAsync(ChatActivity activity, ChannelBotRegistrationEntry registration,
        ConversationTurnRuntimeContext runtimeContext, CancellationToken ct)
    {
        if (activity.Conversation?.Scope is not (ConversationScope.Group or ConversationScope.Channel or ConversationScope.Thread))
            return false;
        if (identityResolver is null || runtimeContext.IsReplyToBot ||
            (!string.IsNullOrWhiteSpace(activity.Content?.Text) && activity.Content.Text.TrimStart().StartsWith('/')))
            return true;
        if (activity.Mentions.Count == 0)
            return false;
        var token = string.IsNullOrWhiteSpace(activity.TransportExtras?.NyxUserAccessToken)
            ? runtimeContext.NyxUserAccessToken : activity.TransportExtras.NyxUserAccessToken;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(registration.NyxProviderSlug))
            return true;
        var botOpenId = await identityResolver.ResolveBotOpenIdAsync(registration.NyxProviderSlug, token, ct);
        return string.IsNullOrWhiteSpace(botOpenId) || activity.Mentions.Any(mention =>
            string.Equals(mention.CanonicalId, botOpenId, StringComparison.Ordinal));
    }
}

/// <summary>Explicit Feishu identity for the existing Lark protocol behavior.</summary>
public sealed class FeishuGroupAdmissionPolicy(ILarkBotIdentityResolver? identityResolver = null) : LarkGroupAdmissionPolicy(identityResolver)
{
    public override string Platform => "feishu";
}
