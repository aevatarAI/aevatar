// ─────────────────────────────────────────────────────────────
// LocalActorRuntime - IActorRuntime implementation.
// Create / Get / Destroy / Link / Unlink / RestoreAll
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Aevatar.Helpers;
using Aevatar.Observability;
using Aevatar.Persistence;
using Aevatar.Routing;
using Aevatar.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Actor;

/// <summary>Local actor runtime: creates, destroys, links actors, and manages manifest persistence.</summary>
public sealed class LocalActorRuntime : IActorRuntime
{
    private readonly ConcurrentDictionary<string, LocalActor> _actors = new();
    private readonly IStreamProvider _streams;
    private readonly IServiceProvider _services;
    private readonly ILogger<LocalActorRuntime> _logger;

    /// <summary>Creates local actor runtime.</summary>
    public LocalActorRuntime(IStreamProvider streams, IServiceProvider services, ILogger<LocalActorRuntime>? logger = null)
    {
        _streams = streams; _services = services;
        _logger = logger ?? NullLogger<LocalActorRuntime>.Instance;
    }

    /// <summary>Creates actor for the specified agent type.</summary>
    public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
        CreateAsync(typeof(TAgent), id, ct);

    /// <summary>Creates actor by type, injects dependencies, and persists manifest.</summary>
    public async Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
    {
        var actorId = id ?? AgentId.New(agentType);
        var agent = CreateAgentInstance(agentType);
        var router = new EventRouter(actorId);
        var logger = _services.GetService<ILoggerFactory>()?.CreateLogger(agentType.Name) ?? NullLogger.Instance;
        var publisher = new LocalActorPublisher(actorId, router, _streams);
        var actor = new LocalActor(agent, actorId, router, _streams, logger);

        InjectDependencies(agent, publisher, actorId, logger);

        if (!_actors.TryAdd(actorId, actor))
            throw new InvalidOperationException($"Actor {actorId} already exists");

        // Persist manifest
        var manifestStore = _services.GetService<IAgentManifestStore>();
        if (manifestStore != null)
        {
            var manifest = await manifestStore.LoadAsync(actorId, ct) ?? new AgentManifest { AgentId = actorId };
            manifest.AgentTypeName = agentType.AssemblyQualifiedName ?? agentType.FullName ?? agentType.Name;
            await manifestStore.SaveAsync(actorId, manifest, ct);
        }

        await actor.ActivateAsync(ct);
        AgentMetrics.ActiveActors.Add(1);
        _logger.LogInformation("Actor {Id} ({Type}) created", actorId, agentType.Name);
        return actor;
    }

    /// <summary>Destroys actor and cleans up stream and manifest.</summary>
    public async Task DestroyAsync(string id, CancellationToken ct = default)
    {
        if (!_actors.TryRemove(id, out var actor)) return;
        await actor.DeactivateAsync(ct);
        AgentMetrics.ActiveActors.Add(-1);
        if (_streams is InMemoryStreamProvider msp) msp.RemoveStream(id);
        var manifestStore = _services.GetService<IAgentManifestStore>();
        if (manifestStore != null) await manifestStore.DeleteAsync(id, ct);
        _logger.LogInformation("Actor {Id} destroyed", id);
    }

    /// <summary>Gets actor by ID.</summary>
    public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(_actors.GetValueOrDefault(id));

    /// <summary>Gets all actors.</summary>
    public Task<IReadOnlyList<IActor>> GetAllAsync() => Task.FromResult<IReadOnlyList<IActor>>(_actors.Values.ToList());

    /// <summary>Checks whether actor with specified ID exists.</summary>
    public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

    /// <summary>Creates parent-child link: parent adds child, child subscribes to parent stream.</summary>
    public async Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
    {
        var parent = GetRequired(parentId);
        var child = GetRequired(childId);
        parent.AddChild(childId);
        await child.SubscribeToParentAsync(parentId, ct);
        _logger.LogInformation("Link: {Parent} → {Child}", parentId, childId);
    }

    /// <summary>Removes parent link from child.</summary>
    public async Task UnlinkAsync(string childId, CancellationToken ct = default)
    {
        if (!_actors.TryGetValue(childId, out var child)) return;
        var parentId = await child.GetParentIdAsync();
        if (parentId != null && _actors.TryGetValue(parentId, out var parent))
            parent.RemoveChild(childId);
        await child.UnsubscribeFromParentAsync();
    }

    /// <summary>Restores all unloaded actors from manifest.</summary>
    public async Task RestoreAllAsync(CancellationToken ct = default)
    {
        var manifestStore = _services.GetService<IAgentManifestStore>();
        if (manifestStore == null) return;
        var manifests = await manifestStore.ListAsync(ct);
        foreach (var m in manifests)
        {
            if (_actors.ContainsKey(m.AgentId)) continue;
            var agentType = System.Type.GetType(m.AgentTypeName);
            if (agentType == null) { _logger.LogWarning("Failed to resolve type {Type}", m.AgentTypeName); continue; }
            await CreateAsync(agentType, m.AgentId, ct);
        }
    }

    private LocalActor GetRequired(string id) =>
        _actors.GetValueOrDefault(id) ?? throw new InvalidOperationException($"Actor {id} does not exist");

    private IAgent CreateAgentInstance(System.Type agentType)
    {
        if (_services.GetService(agentType) is IAgent fromDi) return fromDi;
        return Activator.CreateInstance(agentType) as IAgent
            ?? throw new InvalidOperationException($"Unable to create {agentType.Name}");
    }

    private void InjectDependencies(IAgent agent, IEventPublisher publisher, string actorId, ILogger logger)
    {
        if (agent is not GAgentBase gab) return;
        gab.SetId(actorId);
        gab.EventPublisher = publisher;
        gab.Logger = logger;
        gab.Services = _services;
        gab.ManifestStore = _services.GetService<IAgentManifestStore>();
        InjectStateStore(agent);
    }

    private void InjectStateStore(IAgent agent)
    {
        var type = agent.GetType();
        while (type != null)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(GAgentBase<>))
            {
                var stateType = type.GetGenericArguments()[0];
                var storeType = typeof(IStateStore<>).MakeGenericType(stateType);
                var store = _services.GetService(storeType);
                if (store != null) type.GetProperty("StateStore")?.SetValue(agent, store);
                break;
            }
            type = type.BaseType;
        }
    }
}
