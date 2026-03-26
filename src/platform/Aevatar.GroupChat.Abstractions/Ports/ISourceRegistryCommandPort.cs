using Aevatar.GroupChat.Abstractions.Commands;

namespace Aevatar.GroupChat.Abstractions.Ports;

public interface ISourceRegistryCommandPort
{
    Task<GroupCommandAcceptedReceipt> RegisterSourceAsync(RegisterGroupSourceCommand command, CancellationToken ct = default);

    Task<GroupCommandAcceptedReceipt> UpdateSourceTrustAsync(UpdateGroupSourceTrustCommand command, CancellationToken ct = default);
}
