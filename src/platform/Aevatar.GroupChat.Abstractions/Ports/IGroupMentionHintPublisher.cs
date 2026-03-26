namespace Aevatar.GroupChat.Abstractions.Ports;

public interface IGroupMentionHintPublisher
{
    Task PublishAsync(GroupMentionHint hint, CancellationToken ct = default);
}
