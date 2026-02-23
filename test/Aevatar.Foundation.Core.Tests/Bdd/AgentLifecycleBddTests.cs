// ─────────────────────────────────────────────────────────────
// BDD: Agent lifecycle behavior (mandatory Event Sourcing)
// Feature: Agent activation/deactivation with replay-first recovery
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.Foundation.Core.EventSourcing;
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
        agent.Services = new ServiceCollection().BuildServiceProvider();

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
        agent.Services = new ServiceCollection().BuildServiceProvider();

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
        agent.Services = new ServiceCollection().BuildServiceProvider();
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
        agent.Services = new ServiceCollection().BuildServiceProvider();

        // When / Then
        var act = () => agent.ActivateAsync();
        await act.ShouldThrowAsync<InvalidOperationException>();
    }

    private sealed class CounterReplayBehavior : EventSourcingBehavior<CounterState>
    {
        public CounterReplayBehavior(IEventStore eventStore, string agentId)
            : base(eventStore, agentId) { }

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
}
