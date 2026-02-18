namespace Aevatar.CQRS.Runtime.Abstractions.Commands;

public interface ICommandBus
{
    Task EnqueueAsync<TCommand>(
        CommandEnvelope envelope,
        TCommand command,
        CancellationToken ct = default)
        where TCommand : class;
}
