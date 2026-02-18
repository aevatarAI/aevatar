using Aevatar.CQRS.Runtime.Abstractions.Commands;

namespace Aevatar.CQRS.Runtime.Abstractions.Dispatch;

public interface ICommandHandler<in TCommand>
    where TCommand : class
{
    Task HandleAsync(
        CommandEnvelope envelope,
        TCommand command,
        CancellationToken ct = default);
}
