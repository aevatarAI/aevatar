// ─────────────────────────────────────────────────────────────
// GAgentBase<TState> - stateful base class for GAgent.
// State + mandatory EventSourcing + OnStateChanged Hook
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core;

/// <summary>
/// Stateful GAgent base class with Protobuf state and mandatory Event Sourcing lifecycle.
/// </summary>
/// <typeparam name="TState">Protobuf-generated state type.</typeparam>
public abstract class GAgentBase<TState> : GAgentBase, IAgent<TState>
    where TState : class, IMessage<TState>, new()
{
    private TState _state = new();
    private IServiceProvider? _applierServiceProvider;
    private IReadOnlyList<IStateEventApplier<TState>> _appliers = [];

    /// <summary>Mutable agent state, writable only in EventHandler/OnActivateAsync scopes.</summary>
    public TState State
    {
        get => _state;
        protected set { StateGuard.EnsureWritable(); _state = value; }
    }

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
        await OnStateChangedAsync(_state, ct);
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

    /// <summary>
    /// Applies one persisted domain event to state.
    /// Default behavior delegates to registered <see cref="IStateEventApplier{TState}"/> instances.
    /// Override this method for agent-local transition logic.
    /// </summary>
    protected virtual TState TransitionState(TState current, IMessage evt)
    {
        foreach (var applier in ResolveStateEventAppliers())
        {
            if (applier.TryApply(current, evt, out var next))
                return next;
        }

        return current;
    }

    /// <summary>
    /// Persist one domain event, then apply it to in-memory state.
    /// </summary>
    protected Task PersistDomainEventAsync<TEvent>(TEvent evt, CancellationToken ct = default)
        where TEvent : IMessage
    {
        ArgumentNullException.ThrowIfNull(evt);
        return PersistDomainEventsAsync([evt], ct);
    }

    /// <summary>
    /// Persist domain events as one commit, then apply them to in-memory state in order.
    /// </summary>
    protected async Task PersistDomainEventsAsync(
        IEnumerable<IMessage> events,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var domainEvents = events as IMessage[] ?? events.ToArray();
        if (domainEvents.Length == 0)
            return;

        for (var i = 0; i < domainEvents.Length; i++)
            ArgumentNullException.ThrowIfNull(domainEvents[i]);

        var eventSourcing = EnsureEventSourcingConfigured();
        foreach (var evt in domainEvents)
            eventSourcing.RaiseEvent(evt);

        await eventSourcing.ConfirmEventsAsync(ct);

        using var guard = StateGuard.BeginWriteScope();
        foreach (var evt in domainEvents)
            _state = eventSourcing.TransitionState(_state, evt);

        await OnStateChangedAsync(_state, ct);
    }

    private IEventSourcingBehavior<TState> EnsureEventSourcingConfigured()
    {
        if (EventSourcing != null)
            return EventSourcing;

        if (Services?.GetService(typeof(IEventStore)) is IEventStore eventStore)
        {
            var options = Services.GetService<EventSourcingRuntimeOptions>() ?? new EventSourcingRuntimeOptions();
            var snapshotStore = options.EnableSnapshots
                ? Services.GetService<IEventSourcingSnapshotStore<TState>>()
                : null;
            ISnapshotStrategy snapshotStrategy = options.EnableSnapshots && snapshotStore != null
                ? new IntervalSnapshotStrategy(options.SnapshotInterval)
                : NeverSnapshotStrategy.Instance;

            EventSourcing = new AgentBackedEventSourcingBehavior(
                eventStore,
                Id,
                this,
                snapshotStore,
                snapshotStrategy,
                options.EnableEventCompaction,
                options.RetainedEventsAfterSnapshot);
            return EventSourcing;
        }

        throw new InvalidOperationException(
            $"Stateful agent '{GetType().FullName}' requires '{typeof(IEventSourcingBehavior<TState>).FullName}' " +
            $"for actor '{Id}'.");
    }

    private IReadOnlyList<IStateEventApplier<TState>> ResolveStateEventAppliers()
    {
        if (ReferenceEquals(_applierServiceProvider, Services))
            return _appliers;

        _applierServiceProvider = Services;
        if (Services == null)
        {
            _appliers = [];
            return _appliers;
        }

        _appliers = Services
            .GetServices<IStateEventApplier<TState>>()
            .OrderBy(x => x.Order)
            .ToArray();
        return _appliers;
    }

    private sealed class AgentBackedEventSourcingBehavior : EventSourcingBehavior<TState>
    {
        private readonly GAgentBase<TState> _owner;

        public AgentBackedEventSourcingBehavior(
            IEventStore eventStore,
            string agentId,
            GAgentBase<TState> owner,
            IEventSourcingSnapshotStore<TState>? snapshotStore,
            ISnapshotStrategy snapshotStrategy,
            bool enableEventCompaction,
            int retainedEventsAfterSnapshot)
            : base(
                eventStore,
                agentId,
                snapshotStore,
                snapshotStrategy,
                enableEventCompaction: enableEventCompaction,
                retainedEventsAfterSnapshot: retainedEventsAfterSnapshot)
        {
            _owner = owner;
        }

        public override TState TransitionState(TState current, IMessage evt) =>
            _owner.TransitionState(current, evt);
    }
}
