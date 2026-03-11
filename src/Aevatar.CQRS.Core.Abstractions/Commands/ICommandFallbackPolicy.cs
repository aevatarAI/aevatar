namespace Aevatar.CQRS.Core.Abstractions.Commands;

public interface ICommandFallbackPolicy<TCommand>
{
    bool TryCreateFallbackCommand(
        TCommand command,
        Exception exception,
        out TCommand fallbackCommand);
}
