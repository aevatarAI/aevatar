namespace Aevatar.CQRS.Core.Abstractions.Commands;

public interface ICommandEnvelopeFactory<in TCommand>
{
    EventEnvelope CreateEnvelope(TCommand command, CommandContext context);
}
