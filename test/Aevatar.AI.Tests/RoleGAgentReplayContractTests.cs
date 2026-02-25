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
    public async Task ConfigureRoleEvent_ShouldPersistAndReplayRoleState()
    {
        var store = new InMemoryEventStoreForTests();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent1 = CreateAgent(services, "role-replay-contract");
        await agent1.ActivateAsync();
        await agent1.HandleConfigureRoleAgent(new ConfigureRoleAgentEvent
        {
            RoleName = "researcher",
            ProviderName = "mock",
            Model = "m1",
            SystemPrompt = "be helpful",
        });
        await agent1.DeactivateAsync();

        var persisted = await store.GetEventsAsync("role-replay-contract");
        persisted.Should().ContainSingle(x => x.EventType.Contains(nameof(ConfigureRoleAgentEvent), StringComparison.Ordinal));

        var agent2 = CreateAgent(services, "role-replay-contract");
        await agent2.ActivateAsync();

        agent2.State.RoleName.Should().Be(agent1.State.RoleName);
        agent2.RoleName.Should().Be("researcher");
    }

    [Fact]
    public async Task RoleGAgentFactory_ShouldUseEventSourcedConfigurePath()
    {
        var store = new InMemoryEventStoreForTests();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent1 = CreateAgent(services, "role-factory-replay");
        await agent1.ActivateAsync();
        await RoleGAgentFactory.ApplyConfig(agent1, new RoleYamlConfig
        {
            Name = "assistant",
            Provider = "mock",
            SystemPrompt = "system",
        }, services);
        await agent1.DeactivateAsync();

        var persisted = await store.GetEventsAsync("role-factory-replay");
        persisted.Should().ContainSingle(x => x.EventType.Contains(nameof(ConfigureRoleAgentEvent), StringComparison.Ordinal));

        var agent2 = CreateAgent(services, "role-factory-replay");
        await agent2.ActivateAsync();
        agent2.State.RoleName.Should().Be("assistant");
        agent2.RoleName.Should().Be("assistant");
    }

    [Fact]
    public async Task RoleGAgentFactory_ShouldPreserveExplicitZeroTemperature()
    {
        var store = new InMemoryEventStoreForTests();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = CreateAgent(services, "role-factory-temperature-zero");
        await agent.ActivateAsync();
        await RoleGAgentFactory.ApplyConfig(agent, new RoleYamlConfig
        {
            Name = "assistant",
            Provider = "mock",
            SystemPrompt = "system",
            Temperature = 0,
        }, services);

        agent.Config.Temperature.Should().Be(0);

        var persisted = await store.GetEventsAsync("role-factory-temperature-zero");
        persisted.Should().ContainSingle();
        var evt = persisted.Single().EventData.Unpack<ConfigureRoleAgentEvent>();
        evt.HasTemperature.Should().BeTrue();
        evt.Temperature.Should().Be(0);
    }

    [Fact]
    public async Task RoleGAgentFactory_ShouldKeepTemperatureUnset_WhenYamlTemperatureIsMissing()
    {
        var store = new InMemoryEventStoreForTests();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = CreateAgent(services, "role-factory-temperature-null");
        await agent.ActivateAsync();
        await RoleGAgentFactory.ApplyConfig(agent, new RoleYamlConfig
        {
            Name = "assistant",
            Provider = "mock",
            SystemPrompt = "system",
            Temperature = null,
        }, services);

        agent.Config.Temperature.Should().BeNull();

        var persisted = await store.GetEventsAsync("role-factory-temperature-null");
        persisted.Should().ContainSingle();
        var evt = persisted.Single().EventData.Unpack<ConfigureRoleAgentEvent>();
        evt.HasTemperature.Should().BeFalse();
    }

    private static RoleGAgent CreateAgent(IServiceProvider services, string actorId)
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
