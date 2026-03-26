using Aevatar.GroupChat.Abstractions.Queries;

namespace Aevatar.GroupChat.Abstractions.Ports;

public interface IGroupThreadQueryPort
{
    Task<GroupThreadSnapshot?> GetThreadAsync(
        string groupId,
        string threadId,
        CancellationToken ct = default);

    Task<IReadOnlyList<GroupTimelineMessageSnapshot>> GetMentionedMessagesAsync(
        string groupId,
        string threadId,
        string participantAgentId,
        long sinceCursor = 0,
        int take = 50,
        CancellationToken ct = default);
}
