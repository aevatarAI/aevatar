using Aevatar.CQRS.Core.Abstractions.Interactions;

namespace Aevatar.CQRS.Core.Interactions;

public sealed class NoOpCommandFinalizeEmitter<TReceipt, TCompletion, TFrame>
    : ICommandFinalizeEmitter<TReceipt, TCompletion, TFrame>
{
    public Task EmitAsync(
        TReceipt receipt,
        TCompletion completion,
        bool completed,
        Func<TFrame, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default)
    {
        _ = receipt;
        _ = completion;
        _ = completed;
        _ = emitAsync;
        _ = ct;
        return Task.CompletedTask;
    }
}
