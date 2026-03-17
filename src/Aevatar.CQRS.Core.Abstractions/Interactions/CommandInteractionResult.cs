namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public sealed record CommandInteractionResult<TReceipt, TError, TCompletion>
{
    public required bool Succeeded { get; init; }
    public required TError Error { get; init; }
    public TReceipt? Receipt { get; init; }
    public CommandInteractionFinalizeResult<TCompletion>? FinalizeResult { get; init; }

    public static CommandInteractionResult<TReceipt, TError, TCompletion> Success(
        TReceipt receipt,
        CommandInteractionFinalizeResult<TCompletion> finalizeResult)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(finalizeResult);

        return new CommandInteractionResult<TReceipt, TError, TCompletion>
        {
            Succeeded = true,
            Error = default!,
            Receipt = receipt,
            FinalizeResult = finalizeResult,
        };
    }

    public static CommandInteractionResult<TReceipt, TError, TCompletion> Failure(TError error) =>
        new()
        {
            Succeeded = false,
            Error = error,
            Receipt = default,
            FinalizeResult = null,
        };
}
