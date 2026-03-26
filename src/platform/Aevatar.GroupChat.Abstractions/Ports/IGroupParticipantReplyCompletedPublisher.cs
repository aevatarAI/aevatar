namespace Aevatar.GroupChat.Abstractions.Ports;

public interface IGroupParticipantReplyCompletedPublisher
{
    Task PublishAsync(
        GroupParticipantReplyCompletedEvent evt,
        CancellationToken ct = default);
}
