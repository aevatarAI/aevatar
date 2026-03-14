namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public interface ICommandInteractionCleanupTarget<TReceipt, TCompletion>
{
    Task ReleaseAfterInteractionAsync(
        TReceipt receipt,
        CommandInteractionCleanupContext<TCompletion> cleanup,
        CancellationToken ct = default);
}
