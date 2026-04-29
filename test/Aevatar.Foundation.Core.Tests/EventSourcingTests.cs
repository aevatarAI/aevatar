// ─── Event Sourcing (IEventSourcingBehavior / EventSourcingBehavior / SnapshotStrategy) tests ───

using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Persistence;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.Foundation.Core.Tests;

public class EventSourcingBehaviorTests
{
    private static (IEventStore Store, EventSourcingBehavior<CounterState> Behavior) Create(string agentId = "agent-1")
    {
        var store = new InMemoryEventStore();
        var behavior = new CounterEventSourcingBehavior(store, agentId);
        return (store, behavior);
    }

    [Fact]
    public void CurrentVersion_InitiallyZero()
    {
        var (_, behavior) = Create();
        behavior.CurrentVersion.ShouldBe(0);
    }

    [Fact]
    public async Task ConfirmEventsAsync_WhenNoPending_DoesNothing()
    {
        var (store, behavior) = Create();
        await behavior.ConfirmEventsAsync();
        behavior.CurrentVersion.ShouldBe(0);
        var events = await store.GetEventsAsync("agent-1");
        events.Count.ShouldBe(0);
    }

    [Fact]
    public async Task ConfirmEventsAsync_WhenStateSnapshotEventRaised_ShouldFailFast()
    {
        var (_, behavior) = Create();
        behavior.RaiseEvent(new CounterState { Count = 1, Name = "snapshot" });

        Func<Task> act = async () => await behavior.ConfirmEventsAsync();

        await act.ShouldThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RaiseEvent_And_ConfirmEventsAsync_PersistsAndUpdatesVersion()
    {
        var (store, behavior) = Create();
        behavior.RaiseEvent(new IncrementEvent { Amount = 1 });
        behavior.RaiseEvent(new IncrementEvent { Amount = 2 });
        await behavior.ConfirmEventsAsync();

        behavior.CurrentVersion.ShouldBe(2);
        var events = await store.GetEventsAsync("agent-1");
        events.Count.ShouldBe(2);
        events[0].Version.ShouldBe(1);
        events[1].Version.ShouldBe(2);
        events[0].AgentId.ShouldBe("agent-1");
        events[0].EventType.ShouldContain("IncrementEvent");
    }

    [Fact]
    public async Task ReplayAsync_WhenNoEvents_ReturnsNull()
    {
        var (_, behavior) = Create();
        var state = await behavior.ReplayAsync("agent-1");
        state.ShouldBeNull();
        behavior.CurrentVersion.ShouldBe(0);
    }

    [Fact]
    public async Task ReplayAsync_AfterConfirm_ReplaysAndAppliesTransitionState()
    {
        var (store, behavior) = Create();
        behavior.RaiseEvent(new IncrementEvent { Amount = 10 });
        behavior.RaiseEvent(new IncrementEvent { Amount = 5 });
        behavior.RaiseEvent(new DecrementEvent { Amount = 3 });
        await behavior.ConfirmEventsAsync();

        var state = await behavior.ReplayAsync("agent-1");
        state.ShouldNotBeNull();
        state!.Count.ShouldBe(10 + 5 - 3);
        behavior.CurrentVersion.ShouldBe(3);
    }

    [Fact]
    public async Task ReplayAsync_WithDifferentAgentId_ReadsThatAgentsEvents()
    {
        var (_, behavior) = Create("agent-2");
        behavior.RaiseEvent(new IncrementEvent { Amount = 7 });
        await behavior.ConfirmEventsAsync();

        var state = await behavior.ReplayAsync("agent-2");
        state.ShouldNotBeNull();
        state!.Count.ShouldBe(7);
    }

    [Fact]
    public async Task MultipleConfirmEventsAsync_AppendsInOrder()
    {
        var (store, behavior) = Create();
        behavior.RaiseEvent(new IncrementEvent { Amount = 1 });
        await behavior.ConfirmEventsAsync();
        behavior.RaiseEvent(new IncrementEvent { Amount = 2 });
        await behavior.ConfirmEventsAsync();

        behavior.CurrentVersion.ShouldBe(2);
        var state = await behavior.ReplayAsync("agent-1");
        state.ShouldNotBeNull();
        state!.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ConfirmEventsAsync_WhenNewEventIsRaisedDuringAppend_ShouldKeepUncommittedSuffix()
    {
        var store = new ReentrantAppendEventStore();
        var behavior = new CounterEventSourcingBehavior(store, "agent-reentrant");
        store.OnFirstAppend = () => behavior.RaiseEvent(new IncrementEvent { Amount = 2 });

        behavior.RaiseEvent(new IncrementEvent { Amount = 1 });

        await behavior.ConfirmEventsAsync();

        behavior.CurrentVersion.ShouldBe(1);
        var firstCommit = await store.GetEventsAsync("agent-reentrant");
        firstCommit.Count.ShouldBe(1);
        firstCommit[0].EventData.Unpack<IncrementEvent>().Amount.ShouldBe(1);

        await behavior.ConfirmEventsAsync();

        behavior.CurrentVersion.ShouldBe(2);
        var replayed = await behavior.ReplayAsync("agent-reentrant");
        replayed.ShouldNotBeNull();
        replayed!.Count.ShouldBe(3);
    }

    [Fact]
    public async Task PersistSnapshotAsync_WhenSnapshotStrategyMatches_ShouldPersistSnapshot()
    {
        var store = new InMemoryEventStore();
        var snapshotStore = new InMemoryEventSourcingSnapshotStore<CounterState>();
        var behavior = new CounterEventSourcingBehavior(
            store,
            "agent-snapshot",
            snapshotStore: snapshotStore,
            snapshotStrategy: new IntervalSnapshotStrategy(1));

        behavior.RaiseEvent(new IncrementEvent { Amount = 9 });
        await behavior.ConfirmEventsAsync();
        await behavior.PersistSnapshotAsync(new CounterState { Count = 9, Name = "snapshot" });

        var snapshot = await snapshotStore.LoadAsync("agent-snapshot");
        snapshot.ShouldNotBeNull();
        snapshot!.Version.ShouldBe(1);
        snapshot.State.Count.ShouldBe(9);
    }

    [Fact]
    public async Task PersistSnapshotAsync_WhenSnapshotSaveFails_ShouldNotThrowAndShouldKeepCommittedEvents()
    {
        var store = new InMemoryEventStore();
        var snapshotStore = new ThrowingSnapshotStore<CounterState>();
        var behavior = new CounterEventSourcingBehavior(
            store,
            "agent-snapshot-fail",
            snapshotStore: snapshotStore,
            snapshotStrategy: new IntervalSnapshotStrategy(1));

        behavior.RaiseEvent(new IncrementEvent { Amount = 3 });
        await behavior.ConfirmEventsAsync();

        await behavior.PersistSnapshotAsync(new CounterState { Count = 3, Name = "snapshot-fail" });

        behavior.CurrentVersion.ShouldBe(1);
        var events = await store.GetEventsAsync("agent-snapshot-fail");
        events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task PersistSnapshotAsync_WhenCompactionEnabled_ShouldDeferDeletion_UntilDeferredCompactionRuns()
    {
        var store = new InMemoryEventStore();
        var snapshotStore = new InMemoryEventSourcingSnapshotStore<CounterState>();
        var scheduler = new DeferredEventStoreCompactionScheduler(store);
        var behavior = new CounterEventSourcingBehavior(
            store,
            "agent-snapshot-compact",
            snapshotStore: snapshotStore,
            snapshotStrategy: new IntervalSnapshotStrategy(1),
            enableEventCompaction: true,
            retainedEventsAfterSnapshot: 0,
            compactionScheduler: scheduler);

        behavior.RaiseEvent(new IncrementEvent { Amount = 4 });
        behavior.RaiseEvent(new IncrementEvent { Amount = 6 });
        await behavior.ConfirmEventsAsync();
        await behavior.PersistSnapshotAsync(new CounterState { Count = 10, Name = "snapshot" });

        var events = await store.GetEventsAsync("agent-snapshot-compact");
        events.Count.ShouldBe(2);

        await scheduler.RunOnIdleAsync("agent-snapshot-compact");

        var version = await store.GetVersionAsync("agent-snapshot-compact");
        var compacted = await store.GetEventsAsync("agent-snapshot-compact");
        version.ShouldBe(2);
        compacted.ShouldBeEmpty();

        var replayed = await behavior.ReplayAsync("agent-snapshot-compact");
        replayed.ShouldNotBeNull();
        replayed!.Count.ShouldBe(10);
        behavior.CurrentVersion.ShouldBe(2);
    }

    [Fact]
    public async Task ConfirmEventsAsync_WhenStoreVersionIsAhead_ShouldRefreshCurrentVersionAndAllowRetryViaHandlerReExecution()
    {
        // Reproduces the prod incident behind issue #502: a previous activation
        // committed up to v=N in the store, but this behavior's in-memory
        // _currentVersion is behind. Without the catch-path refresh + pending
        // cleanup, the runtime envelope retry would either (a) wedge on the
        // same conflict forever, or (b) succeed but with duplicate events.
        // Models the real runtime envelope retry shape: handler runs, raises
        // event, ConfirmEventsAsync conflicts, handler runs AGAIN (envelope
        // redelivery), raises the same event, ConfirmEventsAsync succeeds —
        // exactly one logical event is committed at the refreshed version.
        var store = new InMemoryEventStore();
        var behavior = new CounterEventSourcingBehavior(store, "agent-conflict");

        // Out-of-band commits that leave the store ahead of behavior's view.
        await store.AppendAsync(
            "agent-conflict",
            [BuildEvent(version: 1, amount: 10)],
            expectedVersion: 0);
        await store.AppendAsync(
            "agent-conflict",
            [BuildEvent(version: 2, amount: 20)],
            expectedVersion: 1);

        // First handler invocation: raises event, attempts commit, conflicts.
        behavior.RaiseEvent(new IncrementEvent { Amount = 30 });

        await Should.ThrowAsync<EventStoreOptimisticConcurrencyException>(
            () => behavior.ConfirmEventsAsync());

        behavior.CurrentVersion.ShouldBe(2);

        // Envelope redelivery → handler runs again → raises the same event.
        // Without _pending cleanup in the catch path, this would commit two
        // copies of IncrementEvent { Amount = 30 } at v=3 and v=4.
        behavior.RaiseEvent(new IncrementEvent { Amount = 30 });
        await behavior.ConfirmEventsAsync();

        behavior.CurrentVersion.ShouldBe(3);
        var version = await store.GetVersionAsync("agent-conflict");
        version.ShouldBe(3);
        var events = await store.GetEventsAsync("agent-conflict");
        events.Count.ShouldBe(3);
        events[^1].Version.ShouldBe(3);
        events[^1].EventData.Unpack<IncrementEvent>().Amount.ShouldBe(30);
        // Critical assertion: only ONE Amount=30 event in the stream, not two.
        events.Count(evt => evt.EventData.Unpack<IncrementEvent>().Amount == 30).ShouldBe(1);
    }

    [Fact]
    public async Task ConfirmEventsAsync_OnConflict_DropsRejectedBatchFromPendingSoEnvelopeRetryDoesNotDuplicate()
    {
        // Direct regression for the duplicate-events-on-conflict-retry bug
        // raised in PR #503 review (comment 3158219396): leaving _pending
        // intact across the conflict, then relying on Orleans envelope
        // redelivery to re-execute the handler, produced two copies of one
        // logical event in the stream. Asserting the catch path actively
        // clears the rejected batch keeps the contract honest.
        var store = new InMemoryEventStore();
        var behavior = new CounterEventSourcingBehavior(store, "agent-pending-cleanup");

        await store.AppendAsync(
            "agent-pending-cleanup",
            [BuildEvent(version: 1, amount: 7)],
            expectedVersion: 0);

        behavior.RaiseEvent(new IncrementEvent { Amount = 99 });

        await Should.ThrowAsync<EventStoreOptimisticConcurrencyException>(
            () => behavior.ConfirmEventsAsync());

        // After conflict, _pending should be empty — calling ConfirmEventsAsync
        // again without re-raising must be a no-op (no events to commit).
        var noop = await behavior.ConfirmEventsAsync();
        noop.LatestVersion.ShouldBe(1);
        var afterNoop = await store.GetEventsAsync("agent-pending-cleanup");
        afterNoop.Count.ShouldBe(1);
        afterNoop[0].EventData.Unpack<IncrementEvent>().Amount.ShouldBe(7);
    }

    [Fact]
    public async Task ConfirmEventsAsync_WhenConflictReportsLowerActualVersion_ShouldNotRewindCurrentVersion()
    {
        // Defensive guard: a store implementation that reports a malformed
        // EventStoreOptimisticConcurrencyException (ActualVersion < the actor's
        // in-memory _currentVersion) must not silently rewind the actor.
        // Rewinding would cause the next commit to assign duplicate event
        // versions and corrupt the stream. The catch path keeps the larger of
        // the two values so the next retry still surfaces a conflict (which is
        // the correct outcome — the underlying store is broken).
        var store = new MalformedConflictEventStore(reportedActualVersion: 1);
        var behavior = new CounterEventSourcingBehavior(store, "agent-malformed");

        // Seed _currentVersion=5 via a successful commit on the inner store
        // before we engage the malformed-conflict mode.
        for (var i = 1; i <= 5; i++)
            behavior.RaiseEvent(new IncrementEvent { Amount = i });
        await behavior.ConfirmEventsAsync();
        behavior.CurrentVersion.ShouldBe(5);

        store.NextAppendThrowsConflict = true;
        behavior.RaiseEvent(new IncrementEvent { Amount = 6 });

        await Should.ThrowAsync<EventStoreOptimisticConcurrencyException>(
            () => behavior.ConfirmEventsAsync());

        // _currentVersion must NOT regress to the malformed ActualVersion=1.
        behavior.CurrentVersion.ShouldBe(5);
    }

    [Fact]
    public async Task ReplayAsync_WhenStoreVersionIsAheadOfEvents_AndRecoveryDisabled_ShouldThrowDriftException()
    {
        // PR #503 review (comment 3158223163): silently advancing
        // _currentVersion past missing committed events is unsafe for
        // arbitrary domain GAgents because it builds new authoritative state
        // on top of facts that were never applied. Default behavior must
        // surface the drift so an operator decides; the projection-scope
        // recovery path is opt-in via RecoverFromVersionDriftOnReplay.
        var store = new InMemoryEventStore();
        var behavior = new CounterEventSourcingBehavior(store, "agent-version-drift");

        behavior.RaiseEvent(new IncrementEvent { Amount = 1 });
        behavior.RaiseEvent(new IncrementEvent { Amount = 2 });
        behavior.RaiseEvent(new IncrementEvent { Amount = 3 });
        behavior.RaiseEvent(new IncrementEvent { Amount = 4 });
        await behavior.ConfirmEventsAsync();

        await store.DeleteEventsUpToAsync("agent-version-drift", 4);

        var freshBehavior = new CounterEventSourcingBehavior(store, "agent-version-drift");

        var ex = await Should.ThrowAsync<EventStoreVersionDriftException>(
            () => freshBehavior.ReplayAsync("agent-version-drift"));
        ex.AgentId.ShouldBe("agent-version-drift");
        ex.ReplayedVersion.ShouldBe(0);
        ex.StoreVersion.ShouldBe(4);
    }

    [Fact]
    public async Task ReplayAsync_WhenStoreVersionIsAheadOfEvents_AndRecoveryEnabled_ShouldUseStoreVersionAsAuthority()
    {
        // The exact production failure mode behind issue #502: after a partial
        // compaction (events sorted set wiped, version key intact) the actor
        // reactivates with no events to replay but the store's version key
        // ahead of the in-memory _currentVersion. With opt-in recovery enabled
        // (e.g. for projection scope actors), ReplayAsync reconciles
        // _currentVersion to the store-authoritative version so the first
        // AppendAsync doesn't conflict permanently.
        var store = new InMemoryEventStore();
        var behavior = new CounterEventSourcingBehavior(store, "agent-version-drift");

        behavior.RaiseEvent(new IncrementEvent { Amount = 1 });
        behavior.RaiseEvent(new IncrementEvent { Amount = 2 });
        behavior.RaiseEvent(new IncrementEvent { Amount = 3 });
        behavior.RaiseEvent(new IncrementEvent { Amount = 4 });
        await behavior.ConfirmEventsAsync();

        // Simulate compaction that drained the events sorted set without
        // rewinding the version key — InMemoryEventStore.DeleteEventsUpToAsync
        // mirrors the Garnet semantics exactly.
        await store.DeleteEventsUpToAsync("agent-version-drift", 4);

        var versionKey = await store.GetVersionAsync("agent-version-drift");
        versionKey.ShouldBe(4);
        (await store.GetEventsAsync("agent-version-drift")).Count.ShouldBe(0);

        var freshBehavior = new CounterEventSourcingBehavior(
            store,
            "agent-version-drift",
            recoverFromVersionDriftOnReplay: true);
        var replayed = await freshBehavior.ReplayAsync("agent-version-drift");

        replayed.ShouldBeNull();
        freshBehavior.CurrentVersion.ShouldBe(4);

        // And subsequent commits proceed from the floor instead of conflicting.
        freshBehavior.RaiseEvent(new IncrementEvent { Amount = 5 });
        await freshBehavior.ConfirmEventsAsync();
        freshBehavior.CurrentVersion.ShouldBe(5);
        (await store.GetVersionAsync("agent-version-drift")).ShouldBe(5);
    }

    [Fact]
    public async Task ReplayAsync_WhenSnapshotPresentAndStoreVersionAhead_AndRecoveryDisabled_ShouldThrowDriftException()
    {
        // Mirror of the no-snapshot drift case with a snapshot present: by
        // default the actor refuses to activate at the store version because
        // events between the snapshot and the store version represent
        // unrecoverable history for non-idempotent transitions.
        var store = new InMemoryEventStore();
        var snapshotStore = new InMemoryEventSourcingSnapshotStore<CounterState>();

        await snapshotStore.SaveAsync(
            "agent-snapshot-drift",
            new EventSourcingSnapshot<CounterState>(
                new CounterState { Count = 6, Name = "snap" },
                Version: 2));

        await store.AppendAsync(
            "agent-snapshot-drift",
            [BuildEvent(version: 1, amount: 1), BuildEvent(version: 2, amount: 2), BuildEvent(version: 3, amount: 3)],
            expectedVersion: 0);
        await store.DeleteEventsUpToAsync("agent-snapshot-drift", 3);

        var behavior = new CounterEventSourcingBehavior(
            store,
            "agent-snapshot-drift",
            snapshotStore: snapshotStore);

        var ex = await Should.ThrowAsync<EventStoreVersionDriftException>(
            () => behavior.ReplayAsync("agent-snapshot-drift"));
        ex.ReplayedVersion.ShouldBe(2);
        ex.StoreVersion.ShouldBe(3);
    }

    [Fact]
    public async Task DefaultEventSourcingBehaviorFactory_AppliesPerAgentRecoveryPredicate()
    {
        // Per-actor opt-in (EventSourcingRuntimeOptions.ShouldRecoverFromVersionDriftOnReplay)
        // is the production wiring point for projection scope actors: they
        // get drift recovery while domain GAgents keep the safe default.
        var store = new InMemoryEventStore();
        var options = new EventSourcingRuntimeOptions
        {
            EnableSnapshots = false,
            EnableEventCompaction = false,
            ShouldRecoverFromVersionDriftOnReplay = id => id.StartsWith("recoverable:", StringComparison.Ordinal),
        };
        var factory = new DefaultEventSourcingBehaviorFactory<CounterState>(store, options);

        await store.AppendAsync(
            "recoverable:agent-1",
            [BuildEvent(version: 1, amount: 5)],
            expectedVersion: 0);
        await store.DeleteEventsUpToAsync("recoverable:agent-1", 1);

        var recoverable = factory.Create("recoverable:agent-1", static (state, _) => state);
        var recovered = await recoverable.ReplayAsync("recoverable:agent-1");
        recovered.ShouldBeNull();
        recoverable.CurrentVersion.ShouldBe(1);

        await store.AppendAsync(
            "strict:agent-2",
            [BuildEvent(version: 1, amount: 7)],
            expectedVersion: 0);
        await store.DeleteEventsUpToAsync("strict:agent-2", 1);

        var strict = factory.Create("strict:agent-2", static (state, _) => state);
        await Should.ThrowAsync<EventStoreVersionDriftException>(
            () => strict.ReplayAsync("strict:agent-2"));
    }

    [Fact]
    public async Task ReplayAsync_WhenSnapshotPresentAndStoreVersionAhead_AndRecoveryEnabled_ShouldUseStoreVersionAsFloor()
    {
        // Same drift shape but with opt-in recovery: the actor activates at
        // the snapshot state with _currentVersion floored to the store
        // version, so the first AppendAsync proceeds without conflict.
        var store = new InMemoryEventStore();
        var snapshotStore = new InMemoryEventSourcingSnapshotStore<CounterState>();

        await snapshotStore.SaveAsync(
            "agent-snapshot-drift",
            new EventSourcingSnapshot<CounterState>(
                new CounterState { Count = 6, Name = "snap" },
                Version: 2));

        await store.AppendAsync(
            "agent-snapshot-drift",
            [BuildEvent(version: 1, amount: 1), BuildEvent(version: 2, amount: 2), BuildEvent(version: 3, amount: 3)],
            expectedVersion: 0);
        await store.DeleteEventsUpToAsync("agent-snapshot-drift", 3);

        var behavior = new CounterEventSourcingBehavior(
            store,
            "agent-snapshot-drift",
            snapshotStore: snapshotStore,
            recoverFromVersionDriftOnReplay: true);

        var replayed = await behavior.ReplayAsync("agent-snapshot-drift");

        replayed.ShouldNotBeNull();
        replayed!.Count.ShouldBe(6);
        behavior.CurrentVersion.ShouldBe(3);
    }

    private static StateEvent BuildEvent(long version, int amount) => new()
    {
        EventId = $"e-{version}",
        Timestamp = TimestampHelper.Now(),
        Version = version,
        EventType = typeof(IncrementEvent).FullName ?? nameof(IncrementEvent),
        EventData = Any.Pack(new IncrementEvent { Amount = amount }),
        AgentId = "agent-conflict",
    };

    [Fact]
    public async Task ReplayAsync_WhenSnapshotExists_ShouldReplayOnlyDeltaEvents()
    {
        var store = new InMemoryEventStore();
        var snapshotStore = new InMemoryEventSourcingSnapshotStore<CounterState>();
        await snapshotStore.SaveAsync(
            "agent-replay-with-snapshot",
            new EventSourcingSnapshot<CounterState>(
                new CounterState { Count = 10, Name = "snap" },
                2));

        await store.AppendAsync(
            "agent-replay-with-snapshot",
            [new StateEvent
            {
                EventId = "e-3",
                Timestamp = TimestampHelper.Now(),
                Version = 3,
                EventType = typeof(IncrementEvent).FullName ?? nameof(IncrementEvent),
                EventData = Any.Pack(new IncrementEvent { Amount = 4 }),
                AgentId = "agent-replay-with-snapshot",
            }],
            expectedVersion: 0);

        var behavior = new CounterEventSourcingBehavior(
            store,
            "agent-replay-with-snapshot",
            snapshotStore: snapshotStore);

        var replayed = await behavior.ReplayAsync("agent-replay-with-snapshot");

        replayed.ShouldNotBeNull();
        replayed!.Count.ShouldBe(14);
        behavior.CurrentVersion.ShouldBe(3);
    }

    [Fact]
    public async Task DefaultTransitionState_ReturnsCurrentUnchanged()
    {
        var store = new InMemoryEventStore();
        var behavior = new EventSourcingBehavior<CounterState>(store, "agent-1");
        behavior.RaiseEvent(new IncrementEvent { Amount = 1 });
        await behavior.ConfirmEventsAsync();

        var state = await behavior.ReplayAsync("agent-1");
        state.ShouldNotBeNull();
        state!.Count.ShouldBe(0);
    }

    /// <summary>Test behavior that applies IncrementEvent/DecrementEvent to CounterState.</summary>
    internal sealed class CounterEventSourcingBehavior : EventSourcingBehavior<CounterState>
    {
        public CounterEventSourcingBehavior(
            IEventStore eventStore,
            string agentId,
            IEventSourcingSnapshotStore<CounterState>? snapshotStore = null,
            ISnapshotStrategy? snapshotStrategy = null,
            bool enableEventCompaction = false,
            int retainedEventsAfterSnapshot = 0,
            IEventStoreCompactionScheduler? compactionScheduler = null,
            bool recoverFromVersionDriftOnReplay = false)
            : base(
                eventStore,
                agentId,
                snapshotStore,
                snapshotStrategy,
                enableEventCompaction: enableEventCompaction,
                retainedEventsAfterSnapshot: retainedEventsAfterSnapshot,
                compactionScheduler: compactionScheduler,
                recoverFromVersionDriftOnReplay: recoverFromVersionDriftOnReplay) { }

        public override CounterState TransitionState(CounterState current, IMessage evt)
            => StateTransitionMatcher
                .Match(current, evt)
                .On<IncrementEvent>((state, inc) => new CounterState
                {
                    Count = state.Count + inc.Amount,
                    Name = state.Name,
                })
                .On<DecrementEvent>((state, dec) => new CounterState
                {
                    Count = state.Count - dec.Amount,
                    Name = state.Name,
                })
                .OrCurrent();
    }

    private sealed class InMemoryEventSourcingSnapshotStore<TState> : IEventSourcingSnapshotStore<TState>
        where TState : class
    {
        private readonly Dictionary<string, EventSourcingSnapshot<TState>> _snapshots = new(StringComparer.Ordinal);

        public Task<EventSourcingSnapshot<TState>?> LoadAsync(string agentId, CancellationToken ct = default)
        {
            _ = ct;
            _snapshots.TryGetValue(agentId, out var snapshot);
            return Task.FromResult(snapshot);
        }

        public Task SaveAsync(string agentId, EventSourcingSnapshot<TState> snapshot, CancellationToken ct = default)
        {
            _ = ct;
            _snapshots[agentId] = snapshot;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSnapshotStore<TState> : IEventSourcingSnapshotStore<TState>
        where TState : class
    {
        public Task<EventSourcingSnapshot<TState>?> LoadAsync(string agentId, CancellationToken ct = default)
        {
            _ = agentId;
            _ = ct;
            return Task.FromResult<EventSourcingSnapshot<TState>?>(null);
        }

        public Task SaveAsync(string agentId, EventSourcingSnapshot<TState> snapshot, CancellationToken ct = default)
        {
            _ = agentId;
            _ = snapshot;
            _ = ct;
            throw new InvalidOperationException("snapshot-store-failure");
        }
    }

    private sealed class MalformedConflictEventStore : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        private readonly long _reportedActualVersion;

        public MalformedConflictEventStore(long reportedActualVersion)
        {
            _reportedActualVersion = reportedActualVersion;
        }

        public bool NextAppendThrowsConflict { get; set; }

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            if (NextAppendThrowsConflict)
            {
                NextAppendThrowsConflict = false;
                throw new EventStoreOptimisticConcurrencyException(
                    agentId,
                    expectedVersion,
                    _reportedActualVersion);
            }
            return _inner.AppendAsync(agentId, events, expectedVersion, ct);
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            _inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            _inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            _inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }

    private sealed class ReentrantAppendEventStore : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();

        public Action? OnFirstAppend { get; set; }

        public async Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var result = await _inner.AppendAsync(agentId, events, expectedVersion, ct);
            var callback = OnFirstAppend;
            OnFirstAppend = null;
            callback?.Invoke();
            return result;
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            _inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            _inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            _inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }
}

/// <summary>Event Sourcing agent: state restored from event replay on activate, handlers RaiseEvent + ConfirmEventsAsync.</summary>
public class EventSourcingCounterAgent : TestGAgentBase<CounterState>
{
    [EventHandler]
    public async Task HandleIncrement(IncrementEvent evt)
    {
        EventSourcing!.RaiseEvent(evt);
        await EventSourcing.ConfirmEventsAsync();
        var replayed = await EventSourcing.ReplayAsync(Id);
        if (replayed != null)
            State = replayed;
    }

    [EventHandler(Priority = 10)]
    public async Task HandleDecrement(DecrementEvent evt)
    {
        EventSourcing!.RaiseEvent(evt);
        await EventSourcing.ConfirmEventsAsync();
        var replayed = await EventSourcing.ReplayAsync(Id);
        if (replayed != null)
            State = replayed;
    }
}

// ─── Event Sourcing Agent integration tests ───

public class EventSourcingAgentTests
{
    private static IServiceProvider MinimalServices()
    {
        var services = new ServiceCollection();
        services.AddRuntimeScheduler();
        services.AddSingleton<IEnumerable<IGAgentExecutionHook>>(Array.Empty<IGAgentExecutionHook>());
        return services.BuildServiceProvider();
    }

    private static (InMemoryEventStore Store, EventSourcingBehaviorTests.CounterEventSourcingBehavior Behavior) CreateBehavior(string agentId = "es-agent-1")
    {
        var store = new InMemoryEventStore();
        var behavior = new EventSourcingBehaviorTests.CounterEventSourcingBehavior(store, agentId);
        return (store, behavior);
    }

    private static void WireAgent(GAgentBase<CounterState> agent)
    {
        agent.Services = MinimalServices();
    }

    [Fact]
    public async Task EventSourcingAgent_Activate_ReplaysFromStore()
    {
        var (store, behavior) = CreateBehavior("agent-replay");
        var agent = new EventSourcingCounterAgent { EventSourcing = behavior };
        agent.SetId("agent-replay");
        WireAgent(agent);

        // Persist events for this agent before activate
        behavior.RaiseEvent(new IncrementEvent { Amount = 5 });
        behavior.RaiseEvent(new DecrementEvent { Amount = 2 });
        await behavior.ConfirmEventsAsync();

        await agent.ActivateAsync();

        agent.State.Count.ShouldBe(3);
    }

    [Fact]
    public async Task EventSourcingAgent_HandleEvent_PersistsAndUpdatesState()
    {
        var (store, behavior) = CreateBehavior("agent-write");
        var agent = new EventSourcingCounterAgent { EventSourcing = behavior };
        agent.SetId("agent-write");
        WireAgent(agent);
        await agent.ActivateAsync();

        await agent.HandleEventAsync(TestHelper.Envelope(new IncrementEvent { Amount = 10 }));
        agent.State.Count.ShouldBe(10);

        await agent.HandleEventAsync(TestHelper.Envelope(new DecrementEvent { Amount = 3 }));
        agent.State.Count.ShouldBe(7);

        var events = await store.GetEventsAsync("agent-write");
        events.Count.ShouldBe(2);
    }

    [Fact]
    public async Task EventSourcingAgent_DeactivateThenReactivate_RestoresFromReplay()
    {
        var (store, behavior) = CreateBehavior("agent-persist");
        var agent1 = new EventSourcingCounterAgent { EventSourcing = behavior };
        agent1.SetId("agent-persist");
        WireAgent(agent1);
        await agent1.ActivateAsync();
        await agent1.HandleEventAsync(TestHelper.Envelope(new IncrementEvent { Amount = 100 }));
        await agent1.HandleEventAsync(TestHelper.Envelope(new DecrementEvent { Amount = 20 }));
        agent1.State.Count.ShouldBe(80);
        await agent1.DeactivateAsync();

        // New agent instance, same store: replay on activate
        var behavior2 = new EventSourcingBehaviorTests.CounterEventSourcingBehavior(store, "agent-persist");
        var agent2 = new EventSourcingCounterAgent { EventSourcing = behavior2 };
        agent2.SetId("agent-persist");
        WireAgent(agent2);
        await agent2.ActivateAsync();

        agent2.State.Count.ShouldBe(80);
    }

    [Fact]
    public async Task EventSourcingAgent_WithoutBehavior_ShouldFailFastOnActivate()
    {
        var agent = new EventSourcingCounterAgent { EventSourcing = null };
        agent.SetId("agent-null-es");
        WireAgent(agent);
        var act = () => agent.ActivateAsync();
        await act.ShouldThrowAsync<InvalidOperationException>();
    }
}

public class StateEventApplierIntegrationTests
{
    [Fact]
    public async Task PersistDomainEventAsync_ShouldUseRegisteredAppliers_ForRuntimeAndReplay()
    {
        var store = new InMemoryEventStore();
        var services = new ServiceCollection()
            .AddRuntimeScheduler()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .AddSingleton<IStateEventApplier<CounterState>, CounterIncrementApplier>()
            .AddSingleton<IStateEventApplier<CounterState>, CounterDecrementApplier>()
            .AddSingleton<IEnumerable<IGAgentExecutionHook>>(Array.Empty<IGAgentExecutionHook>())
            .BuildServiceProvider();

        var agent1 = new ApplierBackedCounterAgent
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<CounterState>>(),
        };
        agent1.SetId("applier-agent");
        await agent1.ActivateAsync();
        await agent1.HandleEventAsync(TestHelper.Envelope(new IncrementEvent { Amount = 8 }));
        await agent1.HandleEventAsync(TestHelper.Envelope(new DecrementEvent { Amount = 3 }));
        agent1.State.Count.ShouldBe(5);
        await agent1.DeactivateAsync();

        var agent2 = new ApplierBackedCounterAgent
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<CounterState>>(),
        };
        agent2.SetId("applier-agent");
        await agent2.ActivateAsync();
        agent2.State.Count.ShouldBe(5);
    }

    [Fact]
    public async Task PersistDomainEventAsync_ShouldPublishCommittedEvents_AsObservation()
    {
        var store = new InMemoryEventStore();
        var publisher = new RecordingEventPublisher();
        var services = new ServiceCollection()
            .AddRuntimeScheduler()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .AddSingleton<IStateEventApplier<CounterState>, CounterIncrementApplier>()
            .AddSingleton<IEnumerable<IGAgentExecutionHook>>(Array.Empty<IGAgentExecutionHook>())
            .BuildServiceProvider();

        var agent = new ApplierBackedCounterAgent
        {
            Services = services,
            EventPublisher = publisher,
            CommittedStateEventPublisher = publisher,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<CounterState>>(),
        };
        agent.SetId("observe-agent");

        await agent.ActivateAsync();
        var inbound = TestHelper.Envelope(new IncrementEvent { Amount = 4 });
        await agent.HandleEventAsync(inbound);

        agent.State.Count.ShouldBe(4);
        publisher.Observed.ShouldHaveSingleItem();
        var published = publisher.Observed[0].Event.ShouldBeOfType<CommittedStateEventPublished>();
        published.StateEvent.ShouldNotBeNull();
        published.StateEvent.AgentId.ShouldBe("observe-agent");
        published.StateEvent.EventData.Unpack<IncrementEvent>().Amount.ShouldBe(4);
        publisher.Observed[0].SourceEnvelope.ShouldBeSameAs(inbound);
    }

    private sealed class ApplierBackedCounterAgent : TestGAgentBase<CounterState>
    {
        [EventHandler]
        public Task HandleIncrement(IncrementEvent evt) =>
            PersistDomainEventAsync(evt);

        [EventHandler]
        public Task HandleDecrement(DecrementEvent evt) =>
            PersistDomainEventAsync(evt);
    }

    private sealed class CounterIncrementApplier
        : StateEventApplierBase<CounterState, IncrementEvent>
    {
        protected override CounterState Apply(CounterState current, IncrementEvent evt) =>
            new()
            {
                Count = current.Count + evt.Amount,
                Name = current.Name,
            };
    }

    private sealed class CounterDecrementApplier
        : StateEventApplierBase<CounterState, DecrementEvent>
    {
        protected override CounterState Apply(CounterState current, DecrementEvent evt) =>
            new()
            {
                Count = current.Count - evt.Amount,
                Name = current.Name,
            };
    }

    private sealed class RecordingEventPublisher : IEventPublisher, ICommittedStateEventPublisher
    {
        public List<(IMessage Event, TopologyAudience Direction, EventEnvelope? SourceEnvelope)> Published { get; } = [];
        public List<(IMessage Event, EventEnvelope? SourceEnvelope)> Observed { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = options;
            ct.ThrowIfCancellationRequested();
            Published.Add((evt, direction, sourceEnvelope));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            _ = evt;
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PublishAsync(
            CommittedStateEventPublished evt,
            ObserverAudience audience = ObserverAudience.CommittedFacts,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
        {
            _ = audience;
            _ = options;
            ct.ThrowIfCancellationRequested();
            Observed.Add((evt, sourceEnvelope));
            return Task.CompletedTask;
        }
    }
}

public class SnapshotStrategyTests
{
    [Fact]
    public void NeverSnapshotStrategy_AlwaysReturnsFalse()
    {
        var strategy = NeverSnapshotStrategy.Instance;
        strategy.ShouldCreateSnapshot(0).ShouldBeFalse();
        strategy.ShouldCreateSnapshot(1).ShouldBeFalse();
        strategy.ShouldCreateSnapshot(100).ShouldBeFalse();
    }

    [Fact]
    public void IntervalSnapshotStrategy_ReturnsTrueAtInterval()
    {
        var strategy = new IntervalSnapshotStrategy(10);
        strategy.ShouldCreateSnapshot(0).ShouldBeFalse();
        strategy.ShouldCreateSnapshot(9).ShouldBeFalse();
        strategy.ShouldCreateSnapshot(10).ShouldBeTrue();
        strategy.ShouldCreateSnapshot(20).ShouldBeTrue();
        strategy.ShouldCreateSnapshot(100).ShouldBeTrue();
    }

    [Fact]
    public void IntervalSnapshotStrategy_ZeroOrNegativeInterval_DefaultsTo100()
    {
        var strategy = new IntervalSnapshotStrategy(0);
        strategy.ShouldCreateSnapshot(100).ShouldBeTrue();
        strategy.ShouldCreateSnapshot(99).ShouldBeFalse();
    }
}
