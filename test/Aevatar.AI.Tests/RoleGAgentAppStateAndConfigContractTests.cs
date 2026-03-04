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

public sealed class RoleGAgentAppStateAndConfigContractTests
{
    [Fact]
    public async Task HandleSetRoleAppState_ShouldNormalizeEmptyCodecToDefault()
    {
        var store = new InMemoryEventStoreForTests();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent1 = CreateAgent(services, "role-app-state-default-codec");
        await agent1.ActivateAsync();
        await agent1.HandleSetRoleAppState(new SetRoleAppStateEvent
        {
            AppState = Any.Pack(new ChatRequestEvent { Prompt = "payload" }),
            AppStateCodec = "",
            AppStateSchemaVersion = 3,
        });
        await agent1.DeactivateAsync();

        var agent2 = CreateAgent(services, "role-app-state-default-codec");
        await agent2.ActivateAsync();

        agent2.State.AppStateCodec.Should().Be(RoleGAgentExtensionContract.AppStateCodecProtobufAny);
        agent2.State.AppStateSchemaVersion.Should().Be(3);
    }

    [Fact]
    public async Task HandleConfigureRoleAgent_ShouldNormalizeEmptyConfigCodecToDefault()
    {
        var store = new InMemoryEventStoreForTests();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = CreateAgent(services, "role-app-config-default-codec");
        await agent.ActivateAsync();
        await agent.HandleConfigureRoleAgent(new ConfigureRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "mock",
            AppConfigJson = "{\"enabled\":true}",
            AppConfigCodec = "",
            AppConfigSchemaVersion = 2,
        });

        agent.Config.AppConfigCodec.Should().Be(RoleGAgentExtensionContract.AppConfigCodecJsonPlain);
        agent.Config.AppConfigSchemaVersion.Should().Be(2);
        agent.State.ConfigOverrides.Should().NotBeNull();
        agent.State.ConfigOverrides.AppConfigJson.Should().Be("{\"enabled\":true}");
        agent.State.ConfigOverrides.AppConfigCodec.Should().Be(RoleGAgentExtensionContract.AppConfigCodecJsonPlain);
        agent.State.ConfigOverrides.AppConfigSchemaVersion.Should().Be(2);
    }

    [Fact]
    public async Task HandleSetRoleAppConfig_ShouldNormalizeEmptyConfigCodecToDefault()
    {
        var store = new InMemoryEventStoreForTests();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = CreateAgent(services, "role-app-config-patch-default-codec");
        await agent.ActivateAsync();
        await agent.HandleSetRoleAppConfig(new SetRoleAppConfigEvent
        {
            AppConfigJson = "{\"enabled\":true}",
            AppConfigCodec = "",
            AppConfigSchemaVersion = 2,
        });

        agent.Config.AppConfigCodec.Should().Be(RoleGAgentExtensionContract.AppConfigCodecJsonPlain);
        agent.Config.AppConfigSchemaVersion.Should().Be(2);
        agent.State.ConfigOverrides.Should().NotBeNull();
        agent.State.ConfigOverrides.AppConfigJson.Should().Be("{\"enabled\":true}");
        agent.State.ConfigOverrides.AppConfigCodec.Should().Be(RoleGAgentExtensionContract.AppConfigCodecJsonPlain);
        agent.State.ConfigOverrides.AppConfigSchemaVersion.Should().Be(2);
    }

    [Fact]
    public async Task HandleSetRoleAppConfig_ShouldPreserveExistingBaseConfiguration()
    {
        var store = new InMemoryEventStoreForTests();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = CreateAgent(services, "role-app-config-patch-preserve");
        await agent.ActivateAsync();
        await agent.HandleConfigureRoleAgent(new ConfigureRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "mock",
            Model = "model-a",
            SystemPrompt = "be helpful",
            Temperature = 0.7,
            MaxTokens = 256,
            MaxToolRounds = 3,
            MaxHistoryMessages = 9,
            StreamBufferCapacity = 33,
        });

        await agent.HandleSetRoleAppConfig(new SetRoleAppConfigEvent
        {
            AppConfigJson = "{\"tenant\":\"a\"}",
            AppConfigCodec = RoleGAgentExtensionContract.AppConfigCodecJsonPlain,
            AppConfigSchemaVersion = 4,
        });

        agent.RoleName.Should().Be("assistant");
        agent.State.RoleName.Should().Be("assistant");

        agent.Config.ProviderName.Should().Be("mock");
        agent.Config.Model.Should().Be("model-a");
        agent.Config.SystemPrompt.Should().Be("be helpful");
        agent.Config.Temperature.Should().Be(0.7);
        agent.Config.MaxTokens.Should().Be(256);
        agent.Config.MaxToolRounds.Should().Be(3);
        agent.Config.MaxHistoryMessages.Should().Be(9);
        agent.Config.StreamBufferCapacity.Should().Be(33);
        agent.Config.AppConfigJson.Should().Be("{\"tenant\":\"a\"}");
        agent.Config.AppConfigCodec.Should().Be(RoleGAgentExtensionContract.AppConfigCodecJsonPlain);
        agent.Config.AppConfigSchemaVersion.Should().Be(4);
        agent.State.ConfigOverrides.Should().NotBeNull();
        agent.State.ConfigOverrides.AppConfigJson.Should().Be("{\"tenant\":\"a\"}");
        agent.State.ConfigOverrides.AppConfigCodec.Should().Be(RoleGAgentExtensionContract.AppConfigCodecJsonPlain);
        agent.State.ConfigOverrides.AppConfigSchemaVersion.Should().Be(4);
    }

    [Fact]
    public void RoleGAgentExtensionContract_CreateAppStateUpdate_ShouldRejectNullPayload()
    {
        Action act = () => RoleGAgentExtensionContract.CreateAppStateUpdate(
            null!,
            appStateSchemaVersion: 1);

        act.Should().Throw<ArgumentNullException>();
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
