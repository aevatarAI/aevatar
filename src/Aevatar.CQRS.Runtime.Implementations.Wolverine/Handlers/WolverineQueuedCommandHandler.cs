using Aevatar.CQRS.Runtime.Abstractions.Commands;
using Aevatar.CQRS.Runtime.Abstractions.Dispatch;
using Wolverine.Attributes;

namespace Aevatar.CQRS.Runtime.Implementations.Wolverine.Handlers;

[LocalQueue("cqrs-commands")]
public sealed class WolverineQueuedCommandHandler
{
    [WolverineHandler]
    public Task Handle(
        QueuedCommandMessage message,
        IQueuedCommandExecutor executor,
        CancellationToken ct) =>
        executor.ExecuteAsync(message, ct);
}
