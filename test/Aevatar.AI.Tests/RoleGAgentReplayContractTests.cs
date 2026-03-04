using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Core;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public class RoleGAgentReplayContractTests
{
    [Fact]
    public async Task InitializeRoleEvent_ShouldPersistAndReplayRoleState()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-init-replay");
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "researcher",
            ProviderName = "mock",
            Model = "m1",
            SystemPrompt = "be helpful",
            MaxToolRounds = 4,
            MaxHistoryMessages = 32,
            StreamBufferCapacity = 128,
        });
        await agent1.DeactivateAsync();

        var persisted = await store.GetEventsAsync("role-init-replay");
        persisted.Should().ContainSingle(x => x.EventType.Contains(nameof(InitializeRoleAgentEvent), StringComparison.Ordinal));

        var agent2 = CreateAgent(services, "role-init-replay");
        await agent2.ActivateAsync();

        agent2.RoleName.Should().Be("researcher");
        agent2.State.RoleName.Should().Be("researcher");
        agent2.EffectiveConfig.ProviderName.Should().Be("mock");
        agent2.EffectiveConfig.Model.Should().Be("m1");
        agent2.EffectiveConfig.SystemPrompt.Should().Be("be helpful");
        agent2.EffectiveConfig.MaxToolRounds.Should().Be(4);
        agent2.EffectiveConfig.MaxHistoryMessages.Should().Be(32);
        agent2.EffectiveConfig.StreamBufferCapacity.Should().Be(128);
    }

    [Fact]
    public async Task InitializeRoleEvent_ShouldPreserveExplicitZeroTemperature()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent = CreateAgent(services, "role-temperature-zero");
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "mock",
            SystemPrompt = "system",
            Temperature = 0,
        });

        agent.EffectiveConfig.Temperature.Should().Be(0);

        var persisted = await store.GetEventsAsync("role-temperature-zero");
        persisted.Should().ContainSingle();
        var evt = persisted.Single().EventData.Unpack<InitializeRoleAgentEvent>();
        evt.HasTemperature.Should().BeTrue();
        evt.Temperature.Should().Be(0);
    }

    [Fact]
    public async Task RoleGAgentFactory_ShouldUseEventSourcedInitializePath()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-factory-replay");
        await agent1.ActivateAsync();
        await RoleGAgentFactory.ApplyInitialization(agent1, new RoleYamlConfig
        {
            Name = "assistant",
            Provider = "mock",
            SystemPrompt = "system",
        }, services);
        await agent1.DeactivateAsync();

        var persisted = await store.GetEventsAsync("role-factory-replay");
        persisted.Should().ContainSingle(x => x.EventType.Contains(nameof(InitializeRoleAgentEvent), StringComparison.Ordinal));

        var agent2 = CreateAgent(services, "role-factory-replay");
        await agent2.ActivateAsync();
        agent2.State.RoleName.Should().Be("assistant");
        agent2.RoleName.Should().Be("assistant");
    }

    private static IServiceProvider BuildServices(InMemoryEventStoreForTests store)
    {
        return new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
    }

    private static RoleGAgent CreateAgent(
        IServiceProvider services,
        string actorId)
    {
        var agent = new RoleGAgent
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        AssignActorId(agent, actorId);
        return agent;
    }

    private static void AssignActorId(RoleGAgent agent, string actorId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [actorId]);
    }
}
