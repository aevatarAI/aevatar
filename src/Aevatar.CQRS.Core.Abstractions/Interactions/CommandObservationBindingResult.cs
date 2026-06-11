namespace Aevatar.CQRS.Core.Abstractions.Interactions;

// Refactor (iter25/cluster-002-observation-lifecycle-core):
//   Old pattern: command target binding mixed live projection/session setup into dispatch preparation.
//   New principle: observation binding has its own result contract and belongs to the interaction lifecycle, not PrepareAsync.
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
