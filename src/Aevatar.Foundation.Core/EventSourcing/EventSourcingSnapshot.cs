namespace Aevatar.Foundation.Core.EventSourcing;

public sealed record EventSourcingSnapshot<TState>(
    TState State,
    long Version)
    where TState : class;
