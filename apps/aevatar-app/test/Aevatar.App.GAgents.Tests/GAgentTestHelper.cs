using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.App.GAgents.Tests;

internal static class GAgentTestHelper
{
    public static (TAgent Agent, IServiceProvider Services) Create<TAgent, TState>(
        string agentId, IServiceProvider? existingServices = null)
        where TAgent : GAgentBase<TState>, new()
        where TState : class, Google.Protobuf.IMessage<TState>, new()
    {
        var services = existingServices ?? BuildServices();

        var agent = new TAgent
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<TState>>(),
        };
        AssignId(agent, agentId);
        return (agent, services);
    }

    public static async Task<TAgent> CreateAndActivate<TAgent, TState>(string agentId)
        where TAgent : GAgentBase<TState>, new()
        where TState : class, Google.Protobuf.IMessage<TState>, new()
    {
        var (agent, _) = Create<TAgent, TState>(agentId);
        await agent.ActivateAsync();
        return agent;
    }

    public static IServiceProvider BuildServices(InMemoryEventStore? store = null)
    {
        store ??= new InMemoryEventStore();
        return new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
    }

    public static Task SendCommandAsync(IAgent agent, IMessage command)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(command),
            PublisherId = "test",
            Direction = EventDirection.Self,
        };
        return agent.HandleEventAsync(envelope);
    }

    private static void AssignId<TState>(GAgentBase<TState> agent, string agentId)
        where TState : class, Google.Protobuf.IMessage<TState>, new()
    {
        var setId = typeof(GAgentBase).GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SetId method not found on GAgentBase");
        setId.Invoke(agent, [agentId]);
    }
}
