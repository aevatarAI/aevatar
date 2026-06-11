namespace Aevatar.CQRS.Core.Abstractions.Interactions;

// Refactor (iter17/cluster-036):
// Old pattern: the generic detached worker drained events and decided workflow cleanup itself.
// New principle: the worker publishes this typed signal, and the target-owned continuation decides finalization.
public abstract record DetachedCommandSignal<TReceipt, TCompletion>(TReceipt Receipt);

public sealed record DetachedCommandCompleted<TReceipt, TCompletion>(
    TReceipt Receipt,
    TCompletion Completion)
    : DetachedCommandSignal<TReceipt, TCompletion>(Receipt);

public sealed record DetachedCommandTimeout<TReceipt, TCompletion>(
    TReceipt Receipt,
    TCompletion Completion)
    : DetachedCommandSignal<TReceipt, TCompletion>(Receipt);
