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
