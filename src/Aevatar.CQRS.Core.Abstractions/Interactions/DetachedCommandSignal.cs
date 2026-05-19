namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public abstract record DetachedCommandSignal<TReceipt, TCompletion>(TReceipt Receipt);

public sealed record DetachedCommandCompleted<TReceipt, TCompletion>(
    TReceipt Receipt,
    TCompletion Completion)
    : DetachedCommandSignal<TReceipt, TCompletion>(Receipt);

public sealed record DetachedCommandTimeout<TReceipt, TCompletion>(
    TReceipt Receipt,
    TCompletion Completion)
    : DetachedCommandSignal<TReceipt, TCompletion>(Receipt);
