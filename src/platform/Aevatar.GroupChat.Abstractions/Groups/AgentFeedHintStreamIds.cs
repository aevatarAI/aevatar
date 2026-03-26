namespace Aevatar.GroupChat.Abstractions.Groups;

public static class AgentFeedHintStreamIds
{
    public static string ForAgent(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return $"group-chat.feed-hint:{agentId.Trim()}";
    }
}
