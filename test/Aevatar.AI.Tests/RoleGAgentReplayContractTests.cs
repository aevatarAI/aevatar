using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Core;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
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

    [Fact]
    public async Task SetRoleAppStateEvent_ShouldPersistAndReplayAppState()
    {
        var store = new InMemoryEventStoreForTests();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var expected = new ChatResponseEvent
        {
            Content = "state-payload",
            SessionId = "s-app-state",
        };

        var agent1 = CreateAgent(services, "role-app-state");
        await agent1.ActivateAsync();
        await agent1.HandleSetRoleAppState(new SetRoleAppStateEvent
        {
            AppState = Any.Pack(expected),
            AppStateCodec = RoleGAgentExtensionContract.AppStateCodecProtobufAny,
            AppStateSchemaVersion = 2,
        });
        await agent1.DeactivateAsync();

        var persisted = await store.GetEventsAsync("role-app-state");
        persisted.Should().ContainSingle(x => x.EventType.Contains(nameof(SetRoleAppStateEvent), StringComparison.Ordinal));

        var agent2 = CreateAgent(services, "role-app-state");
        await agent2.ActivateAsync();

        agent2.State.AppStateCodec.Should().Be(RoleGAgentExtensionContract.AppStateCodecProtobufAny);
        agent2.State.AppStateSchemaVersion.Should().Be(2);
        agent2.State.AppState.Should().NotBeNull();
        agent2.State.AppState.Unpack<ChatResponseEvent>().Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task ConfigureRoleAgent_ShouldPersistAndReplayAppConfig()
    {
        var store = new InMemoryEventStoreForTests();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent1 = CreateAgent(services, "role-app-config");
        await agent1.ActivateAsync();
        await agent1.HandleConfigureRoleAgent(new ConfigureRoleAgentEvent
        {
            RoleName = "config-role",
            ProviderName = "mock",
            AppConfigJson = "{\"tenant\":\"a\"}",
            AppConfigCodec = RoleGAgentExtensionContract.AppConfigCodecJsonPlain,
            AppConfigSchemaVersion = 7,
        });
        await agent1.DeactivateAsync();

        var agent2 = CreateAgent(services, "role-app-config");
        await agent2.ActivateAsync();

        agent2.Config.AppConfigJson.Should().Be("{\"tenant\":\"a\"}");
        agent2.Config.AppConfigCodec.Should().Be(RoleGAgentExtensionContract.AppConfigCodecJsonPlain);
        agent2.Config.AppConfigSchemaVersion.Should().Be(7);
    }

    [Fact]
    public async Task SetRoleAppConfigEvent_ShouldPersistAndReplayPatchedAppConfig()
    {
        var store = new InMemoryEventStoreForTests();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent1 = CreateAgent(services, "role-app-config-patch");
        await agent1.ActivateAsync();
        await agent1.HandleConfigureRoleAgent(new ConfigureRoleAgentEvent
        {
            RoleName = "config-role",
            ProviderName = "mock",
            Model = "model-a",
            SystemPrompt = "be helpful",
            AppConfigJson = "{\"tenant\":\"old\"}",
            AppConfigCodec = RoleGAgentExtensionContract.AppConfigCodecJsonPlain,
            AppConfigSchemaVersion = 1,
        });
        await agent1.HandleSetRoleAppConfig(new SetRoleAppConfigEvent
        {
            AppConfigJson = "{\"tenant\":\"new\"}",
            AppConfigCodec = RoleGAgentExtensionContract.AppConfigCodecJsonPlain,
            AppConfigSchemaVersion = 2,
        });
        await agent1.DeactivateAsync();

        var persisted = await store.GetEventsAsync("role-app-config-patch");
        persisted.Should().Contain(x => x.EventType.Contains(nameof(SetRoleAppConfigEvent), StringComparison.Ordinal));

        var agent2 = CreateAgent(services, "role-app-config-patch");
        await agent2.ActivateAsync();

        agent2.RoleName.Should().Be("config-role");
        // Without framework-level config manifest, non-app config fields stay on class defaults after replay.
        agent2.Config.ProviderName.Should().BeEmpty();
        agent2.Config.Model.Should().BeNull();
        agent2.Config.SystemPrompt.Should().BeEmpty();
        agent2.Config.AppConfigJson.Should().Be("{\"tenant\":\"new\"}");
        agent2.Config.AppConfigCodec.Should().Be(RoleGAgentExtensionContract.AppConfigCodecJsonPlain);
        agent2.Config.AppConfigSchemaVersion.Should().Be(2);
    }

    [Fact]
    public async Task SetRoleAppConfigEvent_ShouldReplayAppConfigFromEventSourcing()
    {
        var store = new InMemoryEventStoreForTests();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent1 = CreateAgent(services, "role-app-config-event-only");
        await agent1.ActivateAsync();
        await agent1.HandleConfigureRoleAgent(new ConfigureRoleAgentEvent
        {
            RoleName = "config-role",
            ProviderName = "mock",
            AppConfigJson = "{\"tenant\":\"base\"}",
            AppConfigCodec = RoleGAgentExtensionContract.AppConfigCodecJsonPlain,
            AppConfigSchemaVersion = 1,
        });
        await agent1.HandleSetRoleAppConfig(new SetRoleAppConfigEvent
        {
            AppConfigJson = "{\"tenant\":\"event\"}",
            AppConfigCodec = RoleGAgentExtensionContract.AppConfigCodecJsonPlain,
            AppConfigSchemaVersion = 2,
        });
        await agent1.DeactivateAsync();

        var persisted = await store.GetEventsAsync("role-app-config-event-only");
        persisted.Should().Contain(x => x.EventType.Contains(nameof(SetRoleAppConfigEvent), StringComparison.Ordinal));

        var agent2 = CreateAgent(services, "role-app-config-event-only");
        await agent2.ActivateAsync();

        // Base provider/model settings come from class defaults.
        agent2.Config.ProviderName.Should().BeEmpty();
        agent2.Config.AppConfigJson.Should().Be("{\"tenant\":\"event\"}");
        agent2.Config.AppConfigCodec.Should().Be(RoleGAgentExtensionContract.AppConfigCodecJsonPlain);
        agent2.Config.AppConfigSchemaVersion.Should().Be(2);
        agent2.State.AppConfigJson.Should().Be("{\"tenant\":\"event\"}");
        agent2.State.AppConfigCodec.Should().Be(RoleGAgentExtensionContract.AppConfigCodecJsonPlain);
        agent2.State.AppConfigSchemaVersion.Should().Be(2);
    }

    [Fact]
    public async Task HandleSetRoleAppState_ShouldRejectUnknownCodec()
    {
        var store = new InMemoryEventStoreForTests();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = CreateAgent(services, "role-app-state-codec-invalid");
        await agent.ActivateAsync();

        var act = () => agent.HandleSetRoleAppState(new SetRoleAppStateEvent
        {
            AppState = Any.Pack(new ChatResponseEvent { Content = "x" }),
            AppStateCodec = "codec/invalid",
            AppStateSchemaVersion = 1,
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await store.GetEventsAsync("role-app-state-codec-invalid")).Should().BeEmpty();
    }

    [Fact]
    public async Task HandleConfigureRoleAgent_ShouldRejectUnknownAppConfigCodec()
    {
        var store = new InMemoryEventStoreForTests();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = CreateAgent(services, "role-app-config-codec-invalid");
        await agent.ActivateAsync();

        var act = () => agent.HandleConfigureRoleAgent(new ConfigureRoleAgentEvent
        {
            RoleName = "invalid-codec",
            ProviderName = "mock",
            AppConfigJson = "{\"x\":1}",
            AppConfigCodec = "codec/invalid",
            AppConfigSchemaVersion = 1,
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await store.GetEventsAsync("role-app-config-codec-invalid")).Should().BeEmpty();
    }

    [Fact]
    public async Task HandleSetRoleAppConfig_ShouldRejectUnknownAppConfigCodec()
    {
        var store = new InMemoryEventStoreForTests();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = CreateAgent(services, "role-app-config-patch-codec-invalid");
        await agent.ActivateAsync();

        var act = () => agent.HandleSetRoleAppConfig(new SetRoleAppConfigEvent
        {
            AppConfigJson = "{\"x\":1}",
            AppConfigCodec = "codec/invalid",
            AppConfigSchemaVersion = 1,
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await store.GetEventsAsync("role-app-config-patch-codec-invalid")).Should().BeEmpty();
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
