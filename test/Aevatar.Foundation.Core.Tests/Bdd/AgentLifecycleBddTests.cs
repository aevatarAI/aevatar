// ─────────────────────────────────────────────────────────────
// BDD: Agent lifecycle behavior (mandatory Event Sourcing)
// Feature: Agent activation/deactivation with replay-first recovery
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.Tests;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.Foundation.Core.Tests.Bdd;

[Trait("Category", "BDD")]
[Trait("Feature", "AgentLifecycle")]
public class AgentLifecycleBddTests
{
    [Fact(DisplayName = "Given a new Agent with EventSourcing, when activated, State should be initialized to default values")]
    public async Task Given_NewAgentWithEventSourcing_When_Activated_Then_StateIsDefault()
    {
        // Given
        var store = new InMemoryEventStore();
        var behavior = new CounterReplayBehavior(store, "lifecycle-1");
        var agent = new CounterAgent
        {
            EventSourcing = behavior,
        };
        agent.SetId("lifecycle-1");
        agent.Services = TestRuntimeServices.BuildProvider();

        // When
        await agent.ActivateAsync();

        // Then
        agent.State.ShouldNotBeNull();
        agent.State.Count.ShouldBe(0);
        agent.State.Name.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Given an Agent with EventStore history, when activated, should recover state by replay")]
    public async Task Given_AgentWithHistory_When_Activated_Then_StateRecoveredFromReplay()
    {
        // Given
        var store = new InMemoryEventStore();
        await store.AppendAsync(
            "lifecycle-2",
            [new StateEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                Timestamp = TimestampHelper.Now(),
                Version = 1,
                EventType = typeof(IncrementEvent).FullName ?? nameof(IncrementEvent),
                EventData = Any.Pack(new IncrementEvent { Amount = 42 }),
                AgentId = "lifecycle-2",
            }],
            expectedVersion: 0);

        var behavior = new CounterReplayBehavior(store, "lifecycle-2");
        var agent = new CounterAgent
        {
            EventSourcing = behavior,
        };
        agent.SetId("lifecycle-2");
        agent.Services = TestRuntimeServices.BuildProvider();

        // When
        await agent.ActivateAsync();

        // Then
        agent.State.Count.ShouldBe(42);
    }

    [Fact(DisplayName = "Given an active Agent, when deactivated, pending events should be committed")]
    public async Task Given_ActiveAgent_When_Deactivated_Then_PendingEventsCommitted()
    {
        // Given
        var store = new InMemoryEventStore();
        var behavior = new CounterReplayBehavior(store, "lifecycle-3");
        var agent = new CounterAgent
        {
            EventSourcing = behavior,
        };
        agent.SetId("lifecycle-3");
        agent.Services = TestRuntimeServices.BuildProvider();
        await agent.ActivateAsync();
        behavior.RaiseEvent(new IncrementEvent { Amount = 7 });

        // When
        await agent.DeactivateAsync();

        // Then
        var events = await store.GetEventsAsync("lifecycle-3");
        events.Count.ShouldBe(1);
        events[0].EventType.ShouldContain(nameof(IncrementEvent));
    }

    [Fact(DisplayName = "Given an Agent without EventSourcing, when activated, should fail fast")]
    public async Task Given_AgentWithoutEventSourcing_When_Activated_Then_FailFast()
    {
        // Given
        var agent = new CounterAgent
        {
            EventSourcing = null,
        };
        agent.SetId("lifecycle-4");
        agent.Services = TestRuntimeServices.BuildProvider();

        // When / Then
        var act = () => agent.ActivateAsync();
        await act.ShouldThrowAsync<InvalidOperationException>();
    }

    [Fact(DisplayName = "Given deactivation flush hits OCC, pending events should be discarded without snapshot and base lifecycle should complete")]
    public async Task Given_DeactivationFlushHitsOcc_When_Deactivated_Then_DiscardsPendingSkipsSnapshotAndRunsBaseLifecycle()
    {
        // Given
        var store = new InMemoryEventStore();
        var snapshotStore = new RecordingSnapshotStore();
        var behavior = new CounterReplayBehavior(
            store,
            "lifecycle-occ",
            snapshotStore,
            new IntervalSnapshotStrategy(1));
        var module = new LifecycleTrackingModule();
        var agent = new CounterAgent
        {
            EventSourcing = behavior,
        };
        agent.SetId("lifecycle-occ");
        agent.Services = TestRuntimeServices.BuildProvider();
        agent.RegisterModule(module);
        await agent.ActivateAsync();
        behavior.RaiseEvent(new IncrementEvent { Amount = 7 });

        await store.AppendAsync(
            "lifecycle-occ",
            [new StateEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                Timestamp = TimestampHelper.Now(),
                Version = 1,
                EventType = typeof(IncrementEvent).FullName ?? nameof(IncrementEvent),
                EventData = Any.Pack(new IncrementEvent { Amount = 100 }),
                AgentId = "lifecycle-occ",
            }],
            expectedVersion: 0);

        // When
        await agent.DeactivateAsync();

        // Then
        var events = await store.GetEventsAsync("lifecycle-occ");
        events.Count.ShouldBe(1);
        events[0].EventData.Unpack<IncrementEvent>().Amount.ShouldBe(100);
        behavior.CurrentVersion.ShouldBe(1);
        snapshotStore.SaveCount.ShouldBe(0);
        module.DisposeCount.ShouldBe(1);

        var noop = await behavior.ConfirmEventsAsync();
        noop.LatestVersion.ShouldBe(1);
        events = await store.GetEventsAsync("lifecycle-occ");
        events.Count.ShouldBe(1);
    }

    private sealed class CounterReplayBehavior : EventSourcingBehavior<CounterState>
    {
        public CounterReplayBehavior(
            IEventStore eventStore,
            string agentId,
            IEventSourcingSnapshotStore<CounterState>? snapshotStore = null,
            ISnapshotStrategy? snapshotStrategy = null)
            : base(eventStore, agentId, snapshotStore, snapshotStrategy) { }

        public override CounterState TransitionState(CounterState current, IMessage evt)
            => StateTransitionMatcher
                .Match(current, evt)
                .On<IncrementEvent>((state, inc) => new CounterState
                {
                    Count = state.Count + inc.Amount,
                    Name = state.Name,
                })
                .OrCurrent();
    }

    private sealed class RecordingSnapshotStore : IEventSourcingSnapshotStore<CounterState>
    {
        public int SaveCount { get; private set; }

        public Task<EventSourcingSnapshot<CounterState>?> LoadAsync(
            string agentId,
            CancellationToken ct = default)
        {
            _ = agentId;
            _ = ct;
            return Task.FromResult<EventSourcingSnapshot<CounterState>?>(null);
        }

        public Task SaveAsync(
            string agentId,
            EventSourcingSnapshot<CounterState> snapshot,
            CancellationToken ct = default)
        {
            _ = agentId;
            _ = snapshot;
            _ = ct;
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class LifecycleTrackingModule : ILifecycleAwareEventModule
    {
        public string Name => "lifecycle-tracking";

        public int Priority => 0;

        public int DisposeCount { get; private set; }

        public bool CanHandle(EventEnvelope envelope)
        {
            _ = envelope;
            return false;
        }

        public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
        {
            _ = envelope;
            _ = ctx;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task InitializeAsync(CancellationToken ct)
        {
            _ = ct;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

}
