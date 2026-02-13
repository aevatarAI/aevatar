// ─────────────────────────────────────────────────────────────
// IEventSourcingBehavior — Event Sourcing mixin interface.
// Agents enable ES by having this behavior injected via DI; no extra inheritance.
// ─────────────────────────────────────────────────────────────

using Google.Protobuf;

namespace Aevatar.EventSourcing;

/// <summary>
/// Event Sourcing behavior. Agents enable ES by having this interface injected via DI.
/// If not injected, ES is not used — pure mixin, no extra base class required.
/// </summary>
public interface IEventSourcingBehavior<TState> where TState : class, IMessage
{
    /// <summary>Current state version.</summary>
    long CurrentVersion { get; }

    /// <summary>Record a pending state-change event.</summary>
    void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage;

    /// <summary>Persist all pending events to IEventStore.</summary>
    Task ConfirmEventsAsync(CancellationToken ct = default);

    /// <summary>Replay events from IEventStore to rebuild state.</summary>
    Task<TState?> ReplayAsync(string agentId, CancellationToken ct = default);

    /// <summary>Pure function: apply event to state and return new state.</summary>
    TState TransitionState(TState current, IMessage evt);
}
