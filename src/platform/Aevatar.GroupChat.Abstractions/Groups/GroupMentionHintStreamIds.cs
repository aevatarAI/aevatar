namespace Aevatar.GroupChat.Abstractions.Groups;

public static class GroupMentionHintStreamIds
{
    public static string ForParticipant(string participantAgentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(participantAgentId);
        return $"group-chat:mention-hints:{participantAgentId.Trim()}";
    }
}
