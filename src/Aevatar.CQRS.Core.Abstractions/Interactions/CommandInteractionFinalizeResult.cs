namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public sealed record CommandInteractionFinalizeResult<TCompletion>(
    TCompletion Completion,
    bool Completed);
