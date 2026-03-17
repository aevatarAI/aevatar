namespace Aevatar.CQRS.Core.Abstractions.Commands;

public sealed record CommandTargetResolution<TTarget, TError>
    where TTarget : class
{
    public required bool Succeeded { get; init; }
    public required TError Error { get; init; }
    public TTarget? Target { get; init; }

    public static CommandTargetResolution<TTarget, TError> Success(TTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return new CommandTargetResolution<TTarget, TError>
        {
            Succeeded = true,
            Error = default!,
            Target = target,
        };
    }

    public static CommandTargetResolution<TTarget, TError> Failure(TError error) =>
        new()
        {
            Succeeded = false,
            Error = error,
            Target = null,
        };
}
