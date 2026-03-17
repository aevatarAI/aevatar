namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public sealed record CommandInteractionCleanupContext<TCompletion>(
    bool ObservedCompleted,
    TCompletion ObservedCompletion,
    CommandDurableCompletionObservation<TCompletion> DurableCompletion);
