namespace Aevatar.CQRS.Core.Abstractions.Commands;

public interface ICommandTargetEnvelopeFactory<in TCommand, in TTarget>
    where TTarget : class, ICommandDispatchTarget
{
    EventEnvelope CreateEnvelope(TCommand command, TTarget target, CommandContext context);
}
