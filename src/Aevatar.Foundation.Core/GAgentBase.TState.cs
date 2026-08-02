// ─────────────────────────────────────────────────────────────
// GAgentBase<TState> - stateful base class for GAgent.
// State + mandatory EventSourcing + OnStateChanged Hook
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.ExceptionServices;

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
    private IServiceProvider? _publicationHookServiceProvider;
    private IReadOnlyList<ICommittedStatePublicationHook> _publicationHooks = [];
    private IReadOnlyList<CommittedStateEventPublished> _unconfirmedPublications = [];

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
    protected override bool DeferLifecycleAwareModuleInitialization => true;

    /// <summary>Activates agent, replays events to restore state, then calls OnActivateAsync.</summary>
    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        await base.ActivateAsync(ct); // Restore modules
        using var guard = StateGuard.BeginWriteScope();
        var eventSourcing = EnsureEventSourcingConfigured();
        var replayed = await eventSourcing.ReplayAsync(Id, ct);
        _state = replayed ?? new TState();
        await OnStateChangedAsync(_state, ct);
        var recoveredPublications = GetPublicationRecovery(eventSourcing)
            ?.PendingCommittedStatePublications ?? [];
        if (recoveredPublications.Count > 0)
        {
            await PublishAndCheckpointAsync(recoveredPublications, ct);
            await eventSourcing.PersistSnapshotAsync(_state, ct);
        }
        await InitializeLifecycleAwareModulesAsync(ct);
        await OnActivateAsync(ct);
    }

    /// <summary>Deactivates agent, flushes pending events, and optionally persists snapshot optimization.</summary>
    public override async Task DeactivateAsync(CancellationToken ct = default)
    {
        var eventSourcing = EnsureEventSourcingConfigured();
        var snapshotCt = ct;
        try
        {
            await OnDeactivateAsync(ct);
            try
            {
                var commitResult = await eventSourcing.ConfirmEventsAsync(ct);
                if (commitResult.CommittedEvents.Count > 0)
                {
                    snapshotCt = CancellationToken.None;
                    var publications = ApplyCommittedEvents(
                        eventSourcing,
                        commitResult,
                        commitResult.CommittedEvents.Select(static x => (IMessage)x.EventData));
                    await OnStateChangedAsync(_state, CancellationToken.None);
                    await PublishAndCheckpointAsync(publications, CancellationToken.None);
                }
            }
            catch (EventStoreOptimisticConcurrencyException)
            {
                // Refactor (iter713/cluster-gagentbase-deactivation-occ-flush-containment):
                // Old pattern: deactivation OCC escaped with stale pending events and could
                // block base lifecycle cleanup. New principle: deactivation-only OCC
                // containment drains stale pending events, skips snapshot, and still runs
                // base shutdown.
                eventSourcing.DiscardPendingEvents();
                return;
            }

            await eventSourcing.PersistSnapshotAsync(_state, snapshotCt);
        }
        finally
        {
            // Refactor (iter713/cluster-gagentbase-deactivation-occ-flush-containment):
            // Old pattern: event-sourcing flush or snapshot failure skipped base lifecycle cleanup.
            // New principle: base cleanup always runs. When cleanup succeeds, the original
            // hook/confirm/snapshot failure propagates. When cleanup itself fails, callers
            // observe the base cleanup exception semantics (for example, AggregateException
            // pinned by AgentLifecycleBddTests:167).
            await base.DeactivateAsync(ct);
        }
    }

    /// <summary>Hook invoked after state changes, useful for CQRS projection.</summary>
    protected virtual Task OnStateChangedAsync(TState state, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>
    /// Runs non-authoritative state-change work after a commit has acquired authority.
    /// Implementations may narrow the supplied cancellation contract, but must not use it
    /// for committed publication, checkpoint, or snapshot recovery.
    /// </summary>
    protected virtual Task OnCommittedStateChangedAsync(TState state, CancellationToken ct) =>
        OnStateChangedAsync(state, ct);

    /// <summary>Activation hook for subclass initialization.</summary>
    protected virtual Task OnActivateAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Deactivation hook for subclass cleanup.</summary>
    protected virtual Task OnDeactivateAsync(CancellationToken ct) => Task.CompletedTask;

    protected override async Task<bool> PrepareEnvelopeHandlingAsync(
        EventEnvelope envelope,
        CancellationToken ct)
    {
        if (!await base.PrepareEnvelopeHandlingAsync(envelope, ct))
            return false;

        if (_unconfirmedPublications.Count == 0)
            return true;

        var pending = _unconfirmedPublications;
        _unconfirmedPublications = [];
        await PublishAndCheckpointAsync(pending, ct);

        return !string.Equals(
            envelope.Runtime?.Retry?.LastErrorType,
            nameof(CommittedStatePublicationException),
            StringComparison.Ordinal);
    }

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
    /// Persist one domain event with framework-mediated OCC absorption. On
    /// <see cref="EventStoreOptimisticConcurrencyException"/>, the framework
    /// drains pending events, replays from the store to refresh
    /// <see cref="State"/>, and then invokes
    /// <paramref name="onOptimisticConcurrencyConflict"/> to let the caller
    /// decide whether the peer's commit already satisfies the intent of
    /// this command. Returning <c>true</c> swallows the conflict as a
    /// successful no-op (see <see cref="State"/> for the post-replay
    /// shape); returning <c>false</c> rethrows so the runtime envelope
    /// retry path re-evaluates against fresh state.
    /// </summary>
    /// <remarks>
    /// This overload exists so OCC absorption is a *commit-bound*
    /// capability — actors cannot replay state outside an active commit
    /// path (CLAUDE.md "抽象一旦能被滥用就等于设计未完成"). The callback
    /// must be a pure decision function over the refreshed
    /// <see cref="State"/>; it must not raise new events, persist
    /// snapshots, or perform external side effects (NyxID DCR / HTTP),
    /// because the framework has already drained pending events for
    /// recovery and any callback-raised events would be committed on the
    /// next handler turn.
    /// </remarks>
    protected async Task PersistDomainEventAsync<TEvent>(
        TEvent evt,
        Func<EventStoreOptimisticConcurrencyException, Task<bool>> onOptimisticConcurrencyConflict,
        CancellationToken ct = default)
        where TEvent : IMessage
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(onOptimisticConcurrencyConflict);

        await PersistDomainEventsAsync([evt], onOptimisticConcurrencyConflict, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Persist domain events with framework-mediated OCC absorption.
    /// </summary>
    protected async Task PersistDomainEventsAsync(
        IEnumerable<IMessage> events,
        Func<EventStoreOptimisticConcurrencyException, Task<bool>> onOptimisticConcurrencyConflict,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(onOptimisticConcurrencyConflict);

        try
        {
            await PersistDomainEventsAsync(events, ct).ConfigureAwait(false);
        }
        catch (EventStoreOptimisticConcurrencyException conflict)
        {
            // ConfirmEventsAsync only removes the committed prefix on OCC;
            // any events raised mid-flight survive as a pending suffix.
            // Drain them before replay so they cannot be silently committed
            // on the next ConfirmEventsAsync (PR #552 review kimi).
            var eventSourcing = EnsureEventSourcingConfigured();
            eventSourcing.DiscardPendingEvents();
            var replayed = await eventSourcing.ReplayAsync(Id, ct).ConfigureAwait(false);
            using (var guard = StateGuard.BeginWriteScope())
            {
                _state = replayed ?? new TState();
            }

            await OnStateChangedAsync(_state, ct).ConfigureAwait(false);

            var absorbed = await onOptimisticConcurrencyConflict(conflict).ConfigureAwait(false);
            if (!absorbed)
                throw;
        }
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

        EventStoreCommitResult commitResult;
        try
        {
            commitResult = await eventSourcing.ConfirmEventsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A canceled command must not leave its uncommitted events queued for the
            // next terminal commit in the same actor turn.
            eventSourcing.DiscardPendingEvents();
            throw;
        }

        var publications = ApplyCommittedEvents(eventSourcing, commitResult, domainEvents);

        // Append cancellation is admission-only. A returned commit result is authoritative,
        // even if the command deadline elapsed while an atomic adapter was committing.
        // State-change hooks are non-authoritative and may still observe the caller deadline;
        // committed publication/checkpoint/snapshot always finish under recovery authority.
        ExceptionDispatchInfo? stateChangeFailure = null;
        try
        {
            await OnCommittedStateChangedAsync(_state, ct);
        }
        catch (Exception ex)
        {
            stateChangeFailure = ExceptionDispatchInfo.Capture(ex);
        }

        await PublishAndCheckpointAsync(publications, CancellationToken.None);
        await eventSourcing.PersistSnapshotAsync(_state, CancellationToken.None);
        stateChangeFailure?.Throw();
    }

    private IEventSourcingBehavior<TState> EnsureEventSourcingConfigured()
    {
        if (EventSourcing != null)
            return EventSourcing;

        if (EventSourcingBehaviorFactory != null)
        {
            EventSourcing = EventSourcingBehaviorFactory.Create(Id, GetType(), TransitionState);
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

    private IReadOnlyList<CommittedStateEventPublished> ApplyCommittedEvents(
        IEventSourcingBehavior<TState> eventSourcing,
        EventStoreCommitResult commitResult,
        IEnumerable<IMessage> domainEvents)
    {
        var events = domainEvents as IMessage[] ?? domainEvents.ToArray();
        if (events.Length != commitResult.CommittedEvents.Count)
        {
            throw new InvalidOperationException(
                $"Event store commit for actor '{Id}' returned {commitResult.CommittedEvents.Count} " +
                $"events for a batch of {events.Length}.");
        }

        var publications = new List<CommittedStateEventPublished>(events.Length);
        using var guard = StateGuard.BeginWriteScope();
        for (var i = 0; i < events.Length; i++)
        {
            _state = eventSourcing.TransitionState(_state, events[i]);
            publications.Add(new CommittedStateEventPublished
            {
                StateEvent = commitResult.CommittedEvents[i].Clone(),
                StateRoot = Any.Pack(_state),
            });
        }

        return publications;
    }

    private async Task PublishAndCheckpointAsync(
        IReadOnlyList<CommittedStateEventPublished> publications,
        CancellationToken ct)
    {
        var recovery = GetPublicationRecovery(EnsureEventSourcingConfigured());
        for (var i = 0; i < publications.Count; i++)
        {
            var publication = publications[i];
            try
            {
                await PublishCommittedStateAsync(publication, ct);
            }
            catch (Exception ex)
            {
                RememberUnconfirmedPublications(publications, i);
                var failure = await TryRecordPublicationFailureAsync(
                    recovery,
                    publication.StateEvent,
                    CommittedStatePublicationFailureStage.AdapterAcceptance,
                    ex,
                    ct);
                throw new CommittedStatePublicationException(
                    Id,
                    publication.StateEvent,
                    CommittedStatePublicationFailureStage.AdapterAcceptance,
                    failure);
            }

            if (recovery == null)
                continue;

            try
            {
                await recovery.ConfirmPublicationAsync(publication.StateEvent, ct);
            }
            catch (Exception ex)
            {
                RememberUnconfirmedPublications(publications, i);
                var failure = await TryRecordPublicationFailureAsync(
                    recovery,
                    publication.StateEvent,
                    CommittedStatePublicationFailureStage.Checkpoint,
                    ex,
                    ct);
                throw new CommittedStatePublicationException(
                    Id,
                    publication.StateEvent,
                    CommittedStatePublicationFailureStage.Checkpoint,
                    failure);
            }
        }
    }

    private void RememberUnconfirmedPublications(
        IReadOnlyList<CommittedStateEventPublished> publications,
        int startIndex)
    {
        _unconfirmedPublications = publications
            .Skip(startIndex)
            .Select(static publication => publication.Clone())
            .ToArray();
    }

    private static ICommittedStatePublicationRecoveryBehavior? GetPublicationRecovery(
        IEventSourcingBehavior<TState> eventSourcing) =>
        eventSourcing as ICommittedStatePublicationRecoveryBehavior;

    private static async Task<Exception> TryRecordPublicationFailureAsync(
        ICommittedStatePublicationRecoveryBehavior? recovery,
        StateEvent stateEvent,
        CommittedStatePublicationFailureStage stage,
        Exception error,
        CancellationToken ct)
    {
        if (recovery == null)
            return error;

        try
        {
            await recovery.RecordPublicationFailureAsync(stateEvent, stage, error, ct);
            return error;
        }
        catch (Exception recordFailure)
        {
            return new AggregateException(
                "Committed-state publication failed and its durable failure record could not be written.",
                error,
                recordFailure);
        }
    }

    /// <summary>
    /// Re-publishes the actor's <em>current</em> committed state to the projection
    /// pipeline <em>without</em> appending a new domain event. This is the
    /// disaster-recovery primitive for rebuilding a current-state readmodel that was
    /// wiped/reset while the authoritative actor state survived: the committed-fact
    /// channel is live-forward-only (no replay-on-attach), so a wiped readmodel is
    /// otherwise unrecoverable until the next real commit. Because a current-state
    /// materializer (<c>ICurrentStateProjectionMaterializer</c>) rebuilds a row from
    /// the <c>state_root</c> snapshot alone, one re-emission of the current state fully
    /// rematerializes the row; projection writes are monotonic covering writes, so this
    /// is a no-op on a healthy readmodel and a rebuild on a wiped one.
    /// </summary>
    /// <param name="stateEventPayload">
    /// The domain-event payload used only for projection routing/activation (it must be
    /// a type the target actor's <c>IProjectionActivationPlanProvider</c> recognizes);
    /// the materialized content comes from the current state snapshot, not this payload.
    /// Reconstruct it from <see cref="State"/> at the call site.
    /// </param>
    /// <remarks>
    /// CONTRACT: this re-broadcasts a committed fact to <em>all</em>
    /// <see cref="ObserverAudience.CommittedFacts"/> consumers of this actor, at the
    /// actor's current committed version with the deterministic synthetic event id
    /// built by <see cref="CommittedStateRepublish.BuildEventId"/>. It is therefore
    /// only safe for facts whose consumers are idempotent w.r.t. version. Consumers
    /// that must only react to genuinely new committed facts recognize the marker
    /// via <see cref="CommittedStateRepublish.IsRepublishEventId"/> and skip the
    /// envelope — the committed-fact audit materializer does this, so audited actor
    /// types may republish without duplicating governance records (the maintenance
    /// action itself is captured by the invoking endpoint's audit). It appends
    /// nothing to the event store (no <c>RaiseEvent</c>/<c>ConfirmEventsAsync</c>).
    /// </remarks>
    protected Task RepublishCommittedStateAsync(IMessage stateEventPayload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateEventPayload);

        var version = EnsureEventSourcingConfigured().CurrentVersion;
        var published = new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = CommittedStateRepublish.BuildEventId(Id, version),
                Version = version,
                EventType = stateEventPayload.Descriptor.FullName,
                EventData = Any.Pack(stateEventPayload),
                AgentId = Id ?? string.Empty,
            },
            StateRoot = Any.Pack(_state),
        };
        return PublishCommittedStateAsync(published, ct);
    }

    private async Task PublishCommittedStateAsync(
        CommittedStateEventPublished published,
        CancellationToken ct)
    {
        const ObserverAudience audience = ObserverAudience.CommittedFacts;
        var context = new CommittedStatePublicationContext
        {
            ActorId = Id,
            ActorType = GetType(),
            Published = published,
            SourceEnvelope = ActiveInboundEnvelope,
            Audience = audience,
        };

        // Refactor (iter18/cluster-006):
        //   Old pattern: command-path projection activation facade with new actor/lifecycle phase
        //   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
        foreach (var hook in ResolveCommittedStatePublicationHooks())
            await hook.BeforePublishAsync(context, ct);

        await CommittedStateEventPublisher.PublishAsync(
            published,
            audience,
            ct,
            ActiveInboundEnvelope);
    }

    private IReadOnlyList<ICommittedStatePublicationHook> ResolveCommittedStatePublicationHooks()
    {
        if (ReferenceEquals(_publicationHookServiceProvider, Services))
            return _publicationHooks;

        _publicationHookServiceProvider = Services;
        if (Services == null)
        {
            _publicationHooks = [];
            return _publicationHooks;
        }

        _publicationHooks =
            Services.GetService(typeof(IEnumerable<ICommittedStatePublicationHook>))
                is IEnumerable<ICommittedStatePublicationHook> hooks
                    ? hooks.ToArray()
                    : [];
        return _publicationHooks;
    }

}
