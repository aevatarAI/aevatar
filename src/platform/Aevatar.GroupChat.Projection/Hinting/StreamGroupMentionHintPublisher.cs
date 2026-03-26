using Aevatar.Foundation.Abstractions;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Projection.Hinting;

public sealed class StreamGroupMentionHintPublisher : IGroupMentionHintPublisher
{
    private readonly IStreamProvider _streamProvider;

    public StreamGroupMentionHintPublisher(IStreamProvider streamProvider)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
    }

    public Task PublishAsync(GroupMentionHint hint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hint);
        return _streamProvider
            .GetStream(GroupMentionHintStreamIds.ForParticipant(hint.ParticipantAgentId))
            .ProduceAsync(hint, ct);
    }
}
