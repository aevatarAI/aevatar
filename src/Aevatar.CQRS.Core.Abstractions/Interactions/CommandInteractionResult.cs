using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public sealed record CommandInteractionResult<TReceipt, TError, TCompletion>
    : RealtimeSessionResult<TReceipt, TError, TCompletion>
{
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
            Completion = finalizeResult.Completion,
            Completed = finalizeResult.Completed,
            FinalizeResult = finalizeResult,
        };
    }

    public static new CommandInteractionResult<TReceipt, TError, TCompletion> Failure(TError error) =>
        new()
        {
            Succeeded = false,
            Error = error,
            Receipt = default,
            FinalizeResult = null,
        };
}
