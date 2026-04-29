using Aevatar.Foundation.Abstractions.Persistence;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Default event sourcing behavior factory bound to runtime persistence options.
/// </summary>
public sealed class DefaultEventSourcingBehaviorFactory<TState>
    : IEventSourcingBehaviorFactory<TState>
    where TState : class, IMessage<TState>, new()
{
    private readonly IEventStore _eventStore;
    private readonly EventSourcingRuntimeOptions _options;
    private readonly IEventSourcingSnapshotStore<TState>? _snapshotStore;
    private readonly IEventStoreCompactionScheduler? _compactionScheduler;
    private readonly ILogger<EventSourcingBehavior<TState>>? _logger;

    public DefaultEventSourcingBehaviorFactory(
        IEventStore eventStore,
        EventSourcingRuntimeOptions? options = null,
        IEventSourcingSnapshotStore<TState>? snapshotStore = null,
        IEventStoreCompactionScheduler? compactionScheduler = null,
        ILogger<EventSourcingBehavior<TState>>? logger = null)
    {
        _eventStore = eventStore;
        _options = options ?? new EventSourcingRuntimeOptions();
        _snapshotStore = snapshotStore;
        _compactionScheduler = compactionScheduler;
        _logger = logger;
    }

    public IEventSourcingBehavior<TState> Create(
        string agentId,
        Func<TState, IMessage, TState> transitionState)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(transitionState);

        var snapshotsEnabled = _options.EnableSnapshots && _snapshotStore != null;
        var snapshotStore = snapshotsEnabled ? _snapshotStore : null;
        ISnapshotStrategy snapshotStrategy = snapshotsEnabled
            ? new IntervalSnapshotStrategy(_options.SnapshotInterval)
            : NeverSnapshotStrategy.Instance;

        var recoverFromVersionDrift = _options.RecoverFromVersionDriftOnReplay
            || (_options.ShouldRecoverFromVersionDriftOnReplay?.Invoke(agentId) ?? false);

        return new DelegatingEventSourcingBehavior(
            _eventStore,
            agentId,
            transitionState,
            snapshotStore,
            snapshotStrategy,
            _logger,
            _options.EnableEventCompaction,
            _options.RetainedEventsAfterSnapshot,
            _compactionScheduler,
            recoverFromVersionDrift);
    }

    private sealed class DelegatingEventSourcingBehavior : EventSourcingBehavior<TState>
    {
        private readonly Func<TState, IMessage, TState> _transitionState;

        public DelegatingEventSourcingBehavior(
            IEventStore eventStore,
            string agentId,
            Func<TState, IMessage, TState> transitionState,
            IEventSourcingSnapshotStore<TState>? snapshotStore,
            ISnapshotStrategy snapshotStrategy,
            ILogger<EventSourcingBehavior<TState>>? logger,
            bool enableEventCompaction,
            int retainedEventsAfterSnapshot,
            IEventStoreCompactionScheduler? compactionScheduler,
            bool recoverFromVersionDriftOnReplay)
            : base(
                eventStore,
                agentId,
                snapshotStore,
                snapshotStrategy,
                logger,
                enableEventCompaction,
                retainedEventsAfterSnapshot,
                compactionScheduler,
                recoverFromVersionDriftOnReplay)
        {
            _transitionState = transitionState;
        }

        public override TState TransitionState(TState current, IMessage evt) =>
            _transitionState(current, evt);
    }
}
