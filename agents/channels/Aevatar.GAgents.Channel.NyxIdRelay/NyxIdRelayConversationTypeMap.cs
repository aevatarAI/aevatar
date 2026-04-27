using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public static class NyxIdRelayConversationTypeMap
{
    public static bool TryMap(string? conversationType, out ConversationScope scope)
    {
        scope = ConversationScope.Unspecified;
        var normalized = conversationType?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        switch (normalized)
        {
            case "private":
                scope = ConversationScope.DirectMessage;
                return true;
            case "group":
            // Telegram supergroups carry distinct semantics on the Bot API but the
            // ChatActivity scope only distinguishes DM / Group / Channel; collapse to
            // Group so supergroup inbound traffic is not rejected as an unsupported
            // conversation type.
            case "supergroup":
                scope = ConversationScope.Group;
                return true;
            case "channel":
                scope = ConversationScope.Channel;
                return true;
            case "device":
                return false;
            default:
                return false;
        }
    }
}
