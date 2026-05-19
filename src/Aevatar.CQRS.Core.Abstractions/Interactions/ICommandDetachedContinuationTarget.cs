namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public interface ICommandDetachedContinuationTarget<TReceipt, TCompletion>
{
    Task PublishDetachedCommandSignalAsync(
        DetachedCommandSignal<TReceipt, TCompletion> signal,
        CancellationToken ct = default);
}
