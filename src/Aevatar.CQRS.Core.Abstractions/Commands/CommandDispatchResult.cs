namespace Aevatar.CQRS.Core.Abstractions.Commands;

public sealed record CommandDispatchResult<TReceipt, TError>
{
    public required bool Succeeded { get; init; }
    public required TError Error { get; init; }
    public TReceipt? Receipt { get; init; }

    public static CommandDispatchResult<TReceipt, TError> Success(TReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        return new CommandDispatchResult<TReceipt, TError>
        {
            Succeeded = true,
            Error = default!,
            Receipt = receipt,
        };
    }

    public static CommandDispatchResult<TReceipt, TError> Failure(TError error) =>
        new()
        {
            Succeeded = false,
            Error = error,
            Receipt = default,
        };
}
