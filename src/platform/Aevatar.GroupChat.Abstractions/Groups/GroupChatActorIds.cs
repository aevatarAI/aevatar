namespace Aevatar.GroupChat.Abstractions.Groups;

public static class GroupChatActorIds
{
    public static string Thread(string groupId, string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return $"group-chat:thread:{groupId}:{threadId}";
    }

    public static string Feed(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return $"group-chat:feed:{agentId}";
    }

    public static string Source(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        return $"group-chat:source:{sourceId}";
    }
}
