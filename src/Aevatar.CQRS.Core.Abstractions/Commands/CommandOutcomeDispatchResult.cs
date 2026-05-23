namespace Aevatar.CQRS.Core.Abstractions.Commands;

public sealed record CommandOutcomeDispatchResult<TReceipt, TError, TOutcome>
{
    public required bool Succeeded { get; init; }
    public required TError Error { get; init; }
    public TReceipt? Receipt { get; init; }
    public TOutcome? Outcome { get; init; }

    public static CommandOutcomeDispatchResult<TReceipt, TError, TOutcome> Success(
        TReceipt receipt,
        TOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(outcome);

        return new CommandOutcomeDispatchResult<TReceipt, TError, TOutcome>
        {
            Succeeded = true,
            Error = default!,
            Receipt = receipt,
            Outcome = outcome,
        };
    }

    public static CommandOutcomeDispatchResult<TReceipt, TError, TOutcome> Failure(TError error) =>
        new()
        {
            Succeeded = false,
            Error = error,
            Receipt = default,
            Outcome = default,
        };
}
