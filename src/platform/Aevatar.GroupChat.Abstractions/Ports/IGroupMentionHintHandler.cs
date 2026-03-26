namespace Aevatar.GroupChat.Abstractions.Ports;

public interface IGroupMentionHintHandler
{
    Task HandleAsync(GroupMentionHint hint, CancellationToken ct = default);
}
