namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public sealed record CommandObservationScopeLeasePreparationResult<TError>
{
    private CommandObservationScopeLeasePreparationResult(
        bool succeeded,
        TError error,
        ICommandObservationScopeLeasePreparationHandle? handle)
    {
        Succeeded = succeeded;
        Error = error;
        Handle = handle;
    }

    public bool Succeeded { get; }

    public TError Error { get; }

    public ICommandObservationScopeLeasePreparationHandle? Handle { get; }

    public static CommandObservationScopeLeasePreparationResult<TError> Success(
        ICommandObservationScopeLeasePreparationHandle? handle = null) =>
        new(true, default!, handle);

    public static CommandObservationScopeLeasePreparationResult<TError> Failure(TError error) =>
        new(false, error, null);
}
