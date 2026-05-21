namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public sealed record CommandObservationBindingResult<TError>
{
    private CommandObservationBindingResult(bool succeeded, TError error)
    {
        Succeeded = succeeded;
        Error = error;
    }

    public bool Succeeded { get; }

    public TError Error { get; }

    public static CommandObservationBindingResult<TError> Success() =>
        new(true, default!);

    public static CommandObservationBindingResult<TError> Failure(TError error) =>
        new(false, error);
}
