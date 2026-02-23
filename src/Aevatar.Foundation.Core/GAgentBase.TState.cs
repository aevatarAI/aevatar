// ─────────────────────────────────────────────────────────────
// GAgentBase<TState> - stateful base class for GAgent.
// State + mandatory EventSourcing + OnStateChanged Hook
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.Foundation.Core;

/// <summary>
/// Stateful GAgent base class with Protobuf state and mandatory Event Sourcing lifecycle.
/// </summary>
/// <typeparam name="TState">Protobuf-generated state type.</typeparam>
public abstract class GAgentBase<TState> : GAgentBase, IAgent<TState>
    where TState : class, IMessage<TState>, new()
{
    private TState _state = new();

    /// <summary>Mutable agent state, writable only in EventHandler/OnActivateAsync scopes.</summary>
    public TState State
    {
        get => _state;
        protected set { StateGuard.EnsureWritable(); _state = value; }
    }

    /// <summary>State persistence store injected by runtime.</summary>
    public IStateStore<TState>? StateStore { get; set; }

    /// <summary>Event Sourcing behavior injected by runtime; required for state recovery and commit.</summary>
    public IEventSourcingBehavior<TState>? EventSourcing { get; set; }

    /// <summary>Activates agent, replays events to restore state, then calls OnActivateAsync.</summary>
    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        await base.ActivateAsync(ct); // Restore modules
        using var guard = StateGuard.BeginWriteScope();
        var eventSourcing = EnsureEventSourcingConfigured();
        var replayed = await eventSourcing.ReplayAsync(Id, ct);
        _state = replayed ?? new TState();
        await OnActivateAsync(ct);
    }

    /// <summary>Deactivates agent, flushes pending events, and optionally persists snapshot optimization.</summary>
    public override async Task DeactivateAsync(CancellationToken ct = default)
    {
        var eventSourcing = EnsureEventSourcingConfigured();
        await OnDeactivateAsync(ct);
        await eventSourcing.ConfirmEventsAsync(ct);
        await eventSourcing.PersistSnapshotAsync(_state, ct);
    }

    /// <summary>Hook invoked after state changes, useful for CQRS projection.</summary>
    protected virtual Task OnStateChangedAsync(TState state, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>Activation hook for subclass initialization.</summary>
    protected virtual Task OnActivateAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Deactivation hook for subclass cleanup.</summary>
    protected virtual Task OnDeactivateAsync(CancellationToken ct) => Task.CompletedTask;

    private IEventSourcingBehavior<TState> EnsureEventSourcingConfigured()
    {
        if (EventSourcing != null)
            return EventSourcing;

        if (Services?.GetService(typeof(IEventStore)) is IEventStore eventStore)
        {
            EventSourcing = new EventSourcingBehavior<TState>(eventStore, Id);
            return EventSourcing;
        }

        throw new InvalidOperationException(
            $"Stateful agent '{GetType().FullName}' requires '{typeof(IEventSourcingBehavior<TState>).FullName}' " +
            $"for actor '{Id}'.");
    }
}
