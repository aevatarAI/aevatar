namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public readonly record struct CommandDurableCompletionObservation<TCompletion>(
    bool HasTerminalCompletion,
    TCompletion Completion)
{
    public static CommandDurableCompletionObservation<TCompletion> Incomplete { get; } =
        new(false, default!);
}
