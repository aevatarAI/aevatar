namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public interface ICommandDurableCompletionResolver<in TReceipt, TCompletion>
{
    Task<CommandDurableCompletionObservation<TCompletion>> ResolveAsync(
        TReceipt receipt,
        CancellationToken ct = default);
}
