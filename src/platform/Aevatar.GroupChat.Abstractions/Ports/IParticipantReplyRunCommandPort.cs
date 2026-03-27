using Aevatar.GroupChat.Abstractions.Commands;

namespace Aevatar.GroupChat.Abstractions.Ports;

public interface IParticipantReplyRunCommandPort
{
    Task<GroupCommandAcceptedReceipt> StartAsync(
        StartParticipantReplyRunCommand command,
        CancellationToken ct = default);

    Task<GroupCommandAcceptedReceipt> CompleteAsync(
        CompleteParticipantReplyRunCommand command,
        CancellationToken ct = default);
}
