namespace Aevatar.CQRS.Runtime.Abstractions.Commands;

public interface ICommandScheduler
{
    Task ScheduleAsync<TCommand>(
        CommandEnvelope envelope,
        TCommand command,
        TimeSpan delay,
        CancellationToken ct = default)
        where TCommand : class;
}
