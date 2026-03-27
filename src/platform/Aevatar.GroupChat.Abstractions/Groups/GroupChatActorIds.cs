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

    public static string ParticipantReplyRun(
        string groupId,
        string threadId,
        string participantAgentId,
        string sourceEventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(participantAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceEventId);
        return $"group-chat:reply-run:{groupId}:{threadId}:{participantAgentId}:{sourceEventId}";
    }
}
