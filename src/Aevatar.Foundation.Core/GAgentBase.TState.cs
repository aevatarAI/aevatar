// ─────────────────────────────────────────────────────────────
// GAgentBase<TState> - stateful base class for GAgent.
// State + mandatory EventSourcing + OnStateChanged Hook
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core;

/// <summary>
/// Stateful GAgent base class with Protobuf state and mandatory Event Sourcing lifecycle.
/// </summary>
/// <typeparam name="TState">Protobuf-generated state type.</typeparam>
public abstract class GAgentBase<TState> : GAgentBase, IAgent<TState>, IEventSourcingFactoryBinding
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

    /// <summary>Factory used to create per-agent event sourcing behavior when not explicitly injected.</summary>
    public IEventSourcingBehaviorFactory<TState>? EventSourcingBehaviorFactory { get; set; }

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

        var commitResult = await eventSourcing.ConfirmEventsAsync(ct);

        using var guard = StateGuard.BeginWriteScope();
        foreach (var evt in domainEvents)
            _state = eventSourcing.TransitionState(_state, evt);

        await OnStateChangedAsync(_state, ct);
        await PublishCommittedDomainEventsAsync(commitResult, ct);
    }

    private IEventSourcingBehavior<TState> EnsureEventSourcingConfigured()
    {
        if (EventSourcing != null)
            return EventSourcing;

        if (EventSourcingBehaviorFactory != null)
        {
            EventSourcing = EventSourcingBehaviorFactory.Create(Id, TransitionState);
            return EventSourcing;
        }

        throw new InvalidOperationException(
            $"Stateful agent '{GetType().FullName}' requires either '{typeof(IEventSourcingBehavior<TState>).FullName}' " +
            $"or explicitly bound '{typeof(IEventSourcingBehaviorFactory<TState>).FullName}' for actor '{Id}'.");
    }

    void IEventSourcingFactoryBinding.BindEventSourcingFactory(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (EventSourcing != null)
            return;

        EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<TState>>();
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

    private async Task PublishCommittedDomainEventsAsync(
        EventStoreCommitResult commitResult,
        CancellationToken ct)
    {
        for (var i = 0; i < commitResult.CommittedEvents.Count; i++)
        {
            await CommittedStateEventPublisher.PublishAsync(
                new CommittedStateEventPublished
                {
                    StateEvent = commitResult.CommittedEvents[i].Clone(),
                },
                ObserverAudience.CommittedFacts,
                ct,
                ActiveInboundEnvelope);
        }
    }

}
