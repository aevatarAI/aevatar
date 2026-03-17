namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public interface ICommandFinalizeEmitter<in TReceipt, in TCompletion, TFrame>
{
    Task EmitAsync(
        TReceipt receipt,
        TCompletion completion,
        bool completed,
        Func<TFrame, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default);
}
