using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Projection.Hinting;

public sealed class NoOpGroupParticipantReplyCompletedPublisher : IGroupParticipantReplyCompletedPublisher
{
    public Task PublishAsync(GroupParticipantReplyCompletedEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return Task.CompletedTask;
    }
}
