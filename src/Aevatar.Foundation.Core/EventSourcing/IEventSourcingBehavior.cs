// ─────────────────────────────────────────────────────────────
// IEventSourcingBehavior — explicit event-first behavior contract.
// Stateful agents must persist domain events and replay them for recovery.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Google.Protobuf;

namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Event Sourcing behavior.
/// Stateful agents persist explicit domain events and recover state from replay.
/// </summary>
public interface IEventSourcingBehavior<TState> where TState : class, IMessage
{
    /// <summary>Current state version.</summary>
    long CurrentVersion { get; }

    /// <summary>Record a pending state-change event.</summary>
    void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage;

    /// <summary>Persist all pending events to IEventStore and return the committed records.</summary>
    Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default);

    /// <summary>Replay events from IEventStore to rebuild state.</summary>
    Task<TState?> ReplayAsync(string agentId, CancellationToken ct = default);
}
