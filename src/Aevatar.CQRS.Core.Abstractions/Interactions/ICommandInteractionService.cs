using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public interface ICommandInteractionService<TCommand, TReceipt, TError, TFrame, TCompletion>
{
    Task<CommandInteractionResult<TReceipt, TError, TCompletion>> ExecuteAsync(
        TCommand command,
        Func<TFrame, CancellationToken, ValueTask> emitAsync,
        Func<TReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default);
}
