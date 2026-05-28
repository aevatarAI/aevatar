namespace Aevatar.CQRS.Core.Abstractions.Commands;

public interface ICommandDispatchService<in TCommand, TReceipt, TError>
{
    Task<CommandDispatchResult<TReceipt, TError>> DispatchAsync(
        TCommand command,
        CancellationToken ct = default);
}
