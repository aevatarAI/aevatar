namespace Aevatar.GroupChat.Abstractions.Groups;

public static class GroupParticipantRuntimeSessionId
{
    private const string Prefix = "group-chat-reply";

    public static string Build(
        string groupId,
        string threadId,
        string topicId,
        string participantAgentId,
        string sourceEventId,
        string replyToMessageId)
    {
        return string.Join(
            "|",
            Prefix,
            Encode(groupId),
            Encode(threadId),
            Encode(topicId),
            Encode(participantAgentId),
            Encode(sourceEventId),
            Encode(replyToMessageId));
    }

    public static bool TryParse(
        string? sessionId,
        out ParsedGroupParticipantRuntimeSession session)
    {
        session = new ParsedGroupParticipantRuntimeSession(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        var parts = sessionId.Split('|');
        if (parts.Length != 7 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
            return false;

        session = new ParsedGroupParticipantRuntimeSession(
            Decode(parts[1]),
            Decode(parts[2]),
            Decode(parts[3]),
            Decode(parts[4]),
            Decode(parts[5]),
            Decode(parts[6]));
        return !(string.IsNullOrWhiteSpace(session.GroupId) ||
                 string.IsNullOrWhiteSpace(session.ThreadId) ||
                 string.IsNullOrWhiteSpace(session.TopicId) ||
                 string.IsNullOrWhiteSpace(session.ParticipantAgentId) ||
                 string.IsNullOrWhiteSpace(session.SourceEventId) ||
                 string.IsNullOrWhiteSpace(session.ReplyToMessageId));
    }

    private static string Encode(string value) =>
        Uri.EscapeDataString(value?.Trim() ?? string.Empty);

    private static string Decode(string value) =>
        Uri.UnescapeDataString(value ?? string.Empty);
}

public sealed record ParsedGroupParticipantRuntimeSession(
    string GroupId,
    string ThreadId,
    string TopicId,
    string ParticipantAgentId,
    string SourceEventId,
    string ReplyToMessageId);
