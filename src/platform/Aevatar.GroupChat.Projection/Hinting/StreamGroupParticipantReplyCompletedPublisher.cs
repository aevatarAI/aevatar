using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Projection.Hinting;

public sealed class StreamGroupParticipantReplyCompletedPublisher : IGroupParticipantReplyCompletedPublisher
{
    private readonly IStreamProvider _streamProvider;

    public StreamGroupParticipantReplyCompletedPublisher(IStreamProvider streamProvider)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
    }

    public Task PublishAsync(GroupParticipantReplyCompletedEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return _streamProvider.GetStream(GroupParticipantReplyCompletedStreamIds.Global).ProduceAsync(evt, ct);
    }
}
