using Aevatar.GroupChat.Abstractions.Commands;

namespace Aevatar.GroupChat.Abstractions.Ports;

public interface IAgentFeedCommandPort
{
    Task<GroupCommandAcceptedReceipt> AcceptSignalAsync(AcceptSignalToFeedCommand command, CancellationToken ct = default);

    Task<GroupCommandAcceptedReceipt> AdvanceCursorAsync(AdvanceFeedCursorCommand command, CancellationToken ct = default);
}
