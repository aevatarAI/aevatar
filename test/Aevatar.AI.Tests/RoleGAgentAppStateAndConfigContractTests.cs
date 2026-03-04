using Aevatar.AI.Abstractions;
using Aevatar.AI.Core;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public class RoleGAgentAppStateAndConfigContractTests
{
    [Fact]
    public async Task HandleInitializeRoleAgent_ShouldWriteConfigOverridesAndEffectiveConfig()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-init-contract");
        await agent.ActivateAsync();

        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "worker",
            ProviderName = "mock",
            Model = "model-a",
            SystemPrompt = "be helpful",
            Temperature = 0.7,
            MaxTokens = 256,
            MaxToolRounds = 3,
            MaxHistoryMessages = 9,
            StreamBufferCapacity = 33,
        });

        agent.RoleName.Should().Be("worker");
        agent.State.ConfigOverrides.Should().NotBeNull();
        agent.State.ConfigOverrides.ProviderName.Should().Be("mock");
        agent.State.ConfigOverrides.Model.Should().Be("model-a");
        agent.State.ConfigOverrides.SystemPrompt.Should().Be("be helpful");
        agent.State.ConfigOverrides.Temperature.Should().Be(0.7);
        agent.State.ConfigOverrides.MaxTokens.Should().Be(256);
        agent.State.ConfigOverrides.MaxToolRounds.Should().Be(3);
        agent.State.ConfigOverrides.MaxHistoryMessages.Should().Be(9);
        agent.State.ConfigOverrides.StreamBufferCapacity.Should().Be(33);

        agent.Config.ProviderName.Should().Be("mock");
        agent.Config.Model.Should().Be("model-a");
        agent.Config.SystemPrompt.Should().Be("be helpful");
        agent.Config.Temperature.Should().Be(0.7);
        agent.Config.MaxTokens.Should().Be(256);
        agent.Config.MaxToolRounds.Should().Be(3);
        agent.Config.MaxHistoryMessages.Should().Be(9);
        agent.Config.StreamBufferCapacity.Should().Be(33);
    }

    [Fact]
    public async Task HandleInitializeRoleAgent_ShouldAllowClearingOptionalFields()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-init-clear");
        await agent.ActivateAsync();

        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "worker",
            ProviderName = "mock",
            SystemPrompt = "prompt",
            Temperature = 0.3,
            MaxTokens = 123,
        });

        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "worker",
            ProviderName = "mock",
            SystemPrompt = "prompt-2",
            MaxTokens = 0,
        });

        agent.Config.Temperature.Should().BeNull();
        agent.Config.MaxTokens.Should().BeNull();
        agent.Config.SystemPrompt.Should().Be("prompt-2");
    }

    private static ServiceProvider BuildServiceProvider()
    {
        return new ServiceCollection()
            .AddSingleton<IEventStore, InMemoryEventStoreForTests>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
    }

    private static RoleGAgent CreateRoleAgent(IServiceProvider provider, string actorId)
    {
        var agent = new RoleGAgent
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };

        var setId = typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        setId.Invoke(agent, [actorId]);
        return agent;
    }
}
