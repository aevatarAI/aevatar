using Aevatar.CQRS.Runtime.Abstractions.Commands;

namespace Aevatar.CQRS.Runtime.Abstractions.Dispatch;

public interface IQueuedCommandExecutor
{
    Task ExecuteAsync(QueuedCommandMessage message, CancellationToken ct = default);
}
