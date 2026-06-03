using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public interface ICommandInteractionService<TCommand, TReceipt, TError, TFrame, TCompletion>
    : IRealtimeSession<TCommand, TReceipt, TError, TFrame, TCompletion>
{
    new Task<CommandInteractionResult<TReceipt, TError, TCompletion>> ExecuteAsync(
        TCommand command,
        Func<TFrame, CancellationToken, ValueTask> emitAsync,
        Func<TReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default);
}
