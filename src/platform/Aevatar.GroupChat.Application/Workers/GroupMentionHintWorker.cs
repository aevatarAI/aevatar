using Aevatar.Foundation.Abstractions;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Application.Workers;

public sealed class GroupMentionHintWorker
{
    private readonly IStreamProvider _streamProvider;
    private readonly IGroupMentionHintHandler _handler;

    public GroupMentionHintWorker(
        IStreamProvider streamProvider,
        IGroupMentionHintHandler handler)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public Task<IAsyncDisposable> SubscribeAsync(
        string participantAgentId,
        CancellationToken ct = default)
    {
        var streamId = GroupMentionHintStreamIds.ForParticipant(participantAgentId);
        return _streamProvider.GetStream(streamId).SubscribeAsync<GroupMentionHint>(
            hint => _handler.HandleAsync(hint, ct),
            ct);
    }
}
