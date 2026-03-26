using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Projection.Hinting;

public sealed class NoOpGroupMentionHintPublisher : IGroupMentionHintPublisher
{
    public Task PublishAsync(GroupMentionHint hint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hint);
        return Task.CompletedTask;
    }
}
