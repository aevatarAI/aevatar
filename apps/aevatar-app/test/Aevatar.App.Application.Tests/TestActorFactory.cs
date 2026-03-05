using System.Reflection;
using Aevatar.App.Application.Services;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.App.Application.Tests;

internal sealed class TestActorFactory : IActorAccessAppService
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<(System.Type, string), object> _agents = new();

    public TestActorFactory(IServiceProvider? services = null)
    {
        _services = services ?? BuildServices();
    }

    public async Task<TAgent> GetOrCreateAgentAsync<TAgent>(string id) where TAgent : class, IAgent
    {
        var key = (typeof(TAgent), id);
        if (!_agents.TryGetValue(key, out var cached))
        {
            cached = await CreateAgentAsync<TAgent>(id);
            _agents[key] = cached;
        }

        return (TAgent)cached;
    }

    public async Task SendCommandAsync<TAgent>(string id, IMessage command, CancellationToken ct = default)
        where TAgent : class, IAgent
    {
        var agent = await GetOrCreateAgentAsync<TAgent>(id);
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(command),
            PublisherId = "test",
            Direction = EventDirection.Self,
        };
        await ((IAgent)agent).HandleEventAsync(envelope, ct);
    }

    public string ResolveActorId<TAgent>(string id) where TAgent : class, IAgent
    {
        var typeName = typeof(TAgent).Name;
        var prefix = typeName.EndsWith("GAgent", StringComparison.Ordinal)
            ? typeName[..^6].ToLowerInvariant()
            : typeName.ToLowerInvariant();
        return $"{prefix}:{id}";
    }

    private async Task<TAgent> CreateAgentAsync<TAgent>(string id)
    {
        var agentType = typeof(TAgent);
        var agent = (TAgent)Activator.CreateInstance(agentType)!;

        var baseType = agentType;
        while (baseType != null && !baseType.IsGenericType)
            baseType = baseType.BaseType;

        if (baseType is { IsGenericType: true })
        {
            var factoryProp = agentType.GetProperty("EventSourcingBehaviorFactory",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (factoryProp is not null)
            {
                var stateType = baseType.GetGenericArguments()[0];
                var factoryImplType = typeof(IEventSourcingBehaviorFactory<>).MakeGenericType(stateType);
                factoryProp.SetValue(agent, _services.GetRequiredService(factoryImplType));
            }

            var servicesProp = agentType.GetProperty("Services",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            servicesProp?.SetValue(agent, _services);
        }

        var namespacedId = $"{agentType.Name}:{id}";
        var setId = typeof(GAgentBase).GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("SetId not found on GAgentBase");
        setId.Invoke(agent, [namespacedId]);

        if (agent is GAgentBase gab)
            await gab.ActivateAsync();

        return agent;
    }

    private static IServiceProvider BuildServices()
    {
        return new ServiceCollection()
            .AddSingleton<IEventStore>(new InMemoryEventStore())
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
    }
}
