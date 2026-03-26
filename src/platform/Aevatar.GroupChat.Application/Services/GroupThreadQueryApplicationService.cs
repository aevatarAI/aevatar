using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Abstractions.Queries;

namespace Aevatar.GroupChat.Application.Services;

public sealed class GroupThreadQueryApplicationService : IGroupThreadQueryPort
{
    private readonly IGroupTimelineQueryPort _queryPort;

    public GroupThreadQueryApplicationService(IGroupTimelineQueryPort queryPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    }

    public Task<GroupThreadSnapshot?> GetThreadAsync(
        string groupId,
        string threadId,
        CancellationToken ct = default) =>
        _queryPort.GetThreadAsync(groupId, threadId, ct);

    public Task<IReadOnlyList<GroupTimelineMessageSnapshot>> GetMentionedMessagesAsync(
        string groupId,
        string threadId,
        string participantAgentId,
        long sinceCursor = 0,
        int take = 50,
        CancellationToken ct = default) =>
        _queryPort.GetMentionedMessagesAsync(groupId, threadId, participantAgentId, sinceCursor, take, ct);
}
