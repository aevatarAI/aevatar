// ─────────────────────────────────────────────────────────────
// BDD: Agent lifecycle behavior
// Feature: Agent activation, deactivation, state loading/saving
// ─────────────────────────────────────────────────────────────

using Shouldly;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core.Tests.Bdd;

[Trait("Category", "BDD")]
[Trait("Feature", "AgentLifecycle")]
public class AgentLifecycleBddTests
{
    [Fact(DisplayName = "Given a new Agent, when activated, State should be initialized to default values")]
    public async Task Given_NewAgent_When_Activated_Then_StateIsDefault()
    {
        // Given
        var agent = new CounterAgent();
        agent.SetId("lifecycle-1");
        agent.Services = new ServiceCollection().BuildServiceProvider();

        // When
        await agent.ActivateAsync();

        // Then
        agent.State.ShouldNotBeNull();
        agent.State.Count.ShouldBe(0);
        agent.State.Name.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Given an Agent configured with StateStore, when activated, should load existing state from Store")]
    public async Task Given_AgentWithStore_When_Activated_Then_StateLoadedFromStore()
    {
        // Given
        var store = new InMemoryStateStore<CounterState>();
        await store.SaveAsync("lifecycle-2", new CounterState { Count = 42, Name = "saved" });

        var agent = new CounterAgent();
        agent.SetId("lifecycle-2");
        agent.StateStore = store;
        agent.Services = new ServiceCollection().BuildServiceProvider();

        // When
        await agent.ActivateAsync();

        // Then
        agent.State.Count.ShouldBe(42);
        agent.State.Name.ShouldBe("saved");
    }

    [Fact(DisplayName = "Given an active Agent, when deactivated, State should be saved to Store")]
    public async Task Given_ActiveAgent_When_Deactivated_Then_StateSavedToStore()
    {
        // Given
        var store = new InMemoryStateStore<CounterState>();
        var agent = new CounterAgent();
        agent.SetId("lifecycle-3");
        agent.StateStore = store;
        agent.Services = new ServiceCollection().BuildServiceProvider();

        await agent.ActivateAsync();
        // Modify state through event
        var envelope = TestHelper.Envelope(new IncrementEvent { Amount = 7 });
        await agent.HandleEventAsync(envelope);

        // When
        await agent.DeactivateAsync();

        // Then
        var saved = await store.LoadAsync("lifecycle-3");
        saved.ShouldNotBeNull();
        saved!.Count.ShouldBe(7);
    }

    [Fact(DisplayName = "Given an Agent without StateStore, when completing full lifecycle, should not throw exception")]
    public async Task Given_AgentWithoutStore_When_FullLifecycle_Then_NoException()
    {
        // Given
        var agent = new CounterAgent();
        agent.SetId("lifecycle-4");
        agent.Services = new ServiceCollection().BuildServiceProvider();

        // When / Then
        await agent.ActivateAsync();
        await agent.HandleEventAsync(TestHelper.Envelope(new IncrementEvent { Amount = 1 }));
        await agent.DeactivateAsync();

        agent.State.Count.ShouldBe(1);
    }
}
