namespace Aevatar.CQRS.Core.Abstractions.Interactions;

// Refactor (iter17/cluster-036):
// Old pattern: detached command infrastructure reached back into targets through generic cleanup callbacks.
// New principle: each target owns its continuation contract and maps detached signals to domain cleanup.
public interface ICommandDetachedContinuationTarget<TReceipt, TCompletion>
{
    Task PublishDetachedCommandSignalAsync(
        DetachedCommandSignal<TReceipt, TCompletion> signal,
        CancellationToken ct = default);
}
