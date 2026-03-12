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
            IEventStoreCompactionScheduler? compactionScheduler = null)
            : base(
                eventStore,
                agentId,
                snapshotStore,
                snapshotStrategy,
                enableEventCompaction: enableEventCompaction,
                retainedEventsAfterSnapshot: retainedEventsAfterSnapshot,
                compactionScheduler: compactionScheduler) { }

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
    public async Task PersistDomainEventAsync_ShouldPublishCommittedEvents_AsObserve()
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
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<CounterState>>(),
        };
        agent.SetId("observe-agent");

        await agent.ActivateAsync();
        var inbound = TestHelper.Envelope(new IncrementEvent { Amount = 4 });
        await agent.HandleEventAsync(inbound);

        agent.State.Count.ShouldBe(4);
        publisher.Published.ShouldHaveSingleItem();
        publisher.Published[0].Direction.ShouldBe(EventDirection.Observe);
        publisher.Published[0].Event.ShouldBeOfType<IncrementEvent>().Amount.ShouldBe(4);
        publisher.Published[0].SourceEnvelope.ShouldBeSameAs(inbound);
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

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<(IMessage Event, EventDirection Direction, EventEnvelope? SourceEnvelope)> Published { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
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
