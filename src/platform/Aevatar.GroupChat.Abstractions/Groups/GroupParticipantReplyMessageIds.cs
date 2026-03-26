namespace Aevatar.GroupChat.Abstractions.Groups;

public static class GroupParticipantReplyMessageIds
{
    public static string FromSource(string participantAgentId, string sourceEventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(participantAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceEventId);
        return $"participant-reply:{participantAgentId.Trim()}:{sourceEventId.Trim()}";
    }
}
