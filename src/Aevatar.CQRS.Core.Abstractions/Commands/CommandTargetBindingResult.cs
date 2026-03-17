namespace Aevatar.CQRS.Core.Abstractions.Commands;

public sealed record CommandTargetBindingResult<TError>
{
    public required bool Succeeded { get; init; }
    public required TError Error { get; init; }

    public static CommandTargetBindingResult<TError> Success() =>
        new()
        {
            Succeeded = true,
            Error = default!,
        };

    public static CommandTargetBindingResult<TError> Failure(TError error) =>
        new()
        {
            Succeeded = false,
            Error = error,
        };
}
