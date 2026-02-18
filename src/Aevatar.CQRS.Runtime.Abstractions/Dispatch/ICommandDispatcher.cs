using Aevatar.CQRS.Runtime.Abstractions.Commands;

namespace Aevatar.CQRS.Runtime.Abstractions.Dispatch;

public interface ICommandDispatcher
{
    Task DispatchAsync(
        CommandEnvelope envelope,
        object command,
        CancellationToken ct = default);
}
