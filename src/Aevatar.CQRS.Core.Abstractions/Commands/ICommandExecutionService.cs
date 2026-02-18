namespace Aevatar.CQRS.Core.Abstractions.Commands;

public interface ICommandExecutionService<TCommand, TStarted, TFrame, TFinalize, TError>
{
    Task<CommandExecutionResult<TStarted, TFinalize, TError>> ExecuteAsync(
        TCommand command,
        Func<TFrame, CancellationToken, ValueTask> emitAsync,
        Func<TStarted, CancellationToken, ValueTask>? onStartedAsync = null,
        CancellationToken ct = default);
}
