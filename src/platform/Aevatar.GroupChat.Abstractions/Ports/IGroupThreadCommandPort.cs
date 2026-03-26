using Aevatar.GroupChat.Abstractions.Commands;

namespace Aevatar.GroupChat.Abstractions.Ports;

public interface IGroupThreadCommandPort
{
    Task<GroupCommandAcceptedReceipt> CreateThreadAsync(
        CreateGroupThreadCommand command,
        CancellationToken ct = default);

    Task<GroupCommandAcceptedReceipt> PostUserMessageAsync(
        PostUserMessageCommand command,
        CancellationToken ct = default);

    Task<GroupCommandAcceptedReceipt> AppendAgentMessageAsync(
        AppendAgentMessageCommand command,
        CancellationToken ct = default);
}
