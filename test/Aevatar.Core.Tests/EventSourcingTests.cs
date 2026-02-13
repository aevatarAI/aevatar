// ─── Event Sourcing (IEventSourcingBehavior / EventSourcingBehavior / SnapshotStrategy) tests ───

using Aevatar.Attributes;
using Aevatar.EventSourcing;
using Aevatar.Hooks;
using Aevatar.Persistence;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.Tests;

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
        public CounterEventSourcingBehavior(IEventStore eventStore, string agentId)
            : base(eventStore, agentId) { }

        public override CounterState TransitionState(CounterState current, IMessage evt)
        {
            if (evt is not Any any) return current;
            if (any.TryUnpack<IncrementEvent>(out var inc))
                return new CounterState { Count = current.Count + inc.Amount, Name = current.Name };
            if (any.TryUnpack<DecrementEvent>(out var dec))
                return new CounterState { Count = current.Count - dec.Amount, Name = current.Name };
            return current;
        }
    }
}

/// <summary>Event Sourcing agent: state restored from event replay on activate, handlers RaiseEvent + ConfirmEventsAsync.</summary>
public class EventSourcingCounterAgent : GAgentBase<CounterState>
{
    /// <summary>Injected by test or runtime; when set, activate replays from store and handlers persist via ES.</summary>
    public IEventSourcingBehavior<CounterState>? EventSourcing { get; set; }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        if (EventSourcing != null)
        {
            var replayed = await EventSourcing.ReplayAsync(Id, ct);
            if (replayed != null)
                State = replayed;
        }
    }

    protected override async Task OnDeactivateAsync(CancellationToken ct)
    {
        if (EventSourcing != null)
            await EventSourcing.ConfirmEventsAsync(ct);
    }

    [EventHandler]
    public async Task HandleIncrement(IncrementEvent evt)
    {
        if (EventSourcing != null)
        {
            EventSourcing.RaiseEvent(evt);
            await EventSourcing.ConfirmEventsAsync();
            var replayed = await EventSourcing.ReplayAsync(Id);
            if (replayed != null)
                State = replayed;
        }
        else
        {
            State = new CounterState { Count = State.Count + evt.Amount, Name = State.Name };
        }
    }

    [EventHandler(Priority = 10)]
    public async Task HandleDecrement(DecrementEvent evt)
    {
        if (EventSourcing != null)
        {
            EventSourcing.RaiseEvent(evt);
            await EventSourcing.ConfirmEventsAsync();
            var replayed = await EventSourcing.ReplayAsync(Id);
            if (replayed != null)
                State = replayed;
        }
        else
        {
            State = new CounterState { Count = State.Count - evt.Amount, Name = State.Name };
        }
    }
}

// ─── Event Sourcing Agent integration tests ───

public class EventSourcingAgentTests
{
    private static IServiceProvider MinimalServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEnumerable<IGAgentHook>>(Array.Empty<IGAgentHook>());
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
    public async Task EventSourcingAgent_WithoutBehavior_WorksInMemoryOnly()
    {
        var agent = new EventSourcingCounterAgent { EventSourcing = null };
        agent.SetId("agent-null-es");
        WireAgent(agent);
        await agent.ActivateAsync();

        await agent.HandleEventAsync(TestHelper.Envelope(new IncrementEvent { Amount = 7 }));
        agent.State.Count.ShouldBe(7);
        await agent.HandleEventAsync(TestHelper.Envelope(new DecrementEvent { Amount = 2 }));
        agent.State.Count.ShouldBe(5);
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
