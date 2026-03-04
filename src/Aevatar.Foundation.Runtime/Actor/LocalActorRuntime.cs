// ─────────────────────────────────────────────────────────────
// LocalActorRuntime - IActorRuntime implementation.
// Create / Get / Destroy / Link / Unlink
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Observability;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Actors;

/// <summary>Local actor runtime: creates, destroys, links actors, and manages manifest persistence.</summary>
public sealed class LocalActorRuntime : IActorRuntime
{
    private readonly ConcurrentDictionary<string, LocalActor> _actors = new();
    private readonly IStreamProvider _streams;
    private readonly IStreamLifecycleManager _streamLifecycleManager;
    private readonly IServiceProvider _services;
    private readonly IActorDeactivationHookDispatcher? _deactivationHookDispatcher;
    private readonly ILogger<LocalActorRuntime> _logger;

    /// <summary>Creates local actor runtime.</summary>
    public LocalActorRuntime(
        IStreamProvider streams,
        IServiceProvider services,
        IStreamLifecycleManager streamLifecycleManager,
        ILogger<LocalActorRuntime>? logger = null)
    {
        _streams = streams;
        _services = services;
        _streamLifecycleManager = streamLifecycleManager;
        _deactivationHookDispatcher = services.GetService<IActorDeactivationHookDispatcher>();
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
        var propagationPolicy = _services.GetService<IEnvelopePropagationPolicy>();
        var publisher = new LocalActorPublisher(actorId, router, _streams, propagationPolicy);
        var actor = new LocalActor(
            agent,
            actorId,
            router,
            _streams,
            logger,
            _deactivationHookDispatcher);

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
        if (!_actors.TryRemove(id, out var actor))
        {
            _streamLifecycleManager.RemoveStream(id);
            var unloadedManifestStore = _services.GetService<IAgentManifestStore>();
            if (unloadedManifestStore != null)
                await unloadedManifestStore.DeleteAsync(id, ct);
            return;
        }

        await actor.DeactivateAsync(ct);
        AgentMetrics.ActiveActors.Add(-1);
        _streamLifecycleManager.RemoveStream(id);
        var manifestStore = _services.GetService<IAgentManifestStore>();
        if (manifestStore != null) await manifestStore.DeleteAsync(id, ct);
        _logger.LogInformation("Actor {Id} destroyed", id);
    }

    /// <summary>Gets actor by ID.</summary>
    public async Task<IActor?> GetAsync(string id)
    {
        var actor = _actors.GetValueOrDefault(id);
        if (actor != null)
            return actor;

        await EnsureActorMaterializedAsync(id);
        return _actors.GetValueOrDefault(id);
    }

    /// <summary>Checks whether actor with specified ID exists.</summary>
    public async Task<bool> ExistsAsync(string id)
    {
        if (_actors.ContainsKey(id))
            return true;

        var manifestStore = _services.GetService<IAgentManifestStore>();
        if (manifestStore == null)
            return false;

        var manifest = await manifestStore.LoadAsync(id);
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.AgentTypeName))
            return false;

        return ResolveAgentType(manifest.AgentTypeName) != null;
    }

    /// <summary>Creates parent-child link and registers stream-layer forwarding binding.</summary>
    public async Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
    {
        var parent = await GetRequiredAsync(parentId);
        var child = await GetRequiredAsync(childId);
        parent.AddChild(childId);
        await child.SubscribeToParentAsync(parentId, ct);
        await _streams.GetStream(parentId).UpsertRelayAsync(
            StreamForwardingRules.CreateHierarchyBinding(parentId, childId),
            ct);

        _logger.LogInformation("Link: {Parent} → {Child}", parentId, childId);
    }

    /// <summary>Removes parent link from child.</summary>
    public async Task UnlinkAsync(string childId, CancellationToken ct = default)
    {
        var child = await GetLocalActorAsync(childId);
        if (child == null)
            return;

        var parentId = await child.GetParentIdAsync();
        if (parentId != null)
        {
            var parent = await GetLocalActorAsync(parentId);
            if (parent != null)
                parent.RemoveChild(childId);

            await _streams.GetStream(parentId).RemoveRelayAsync(childId, ct);
        }

        await child.UnsubscribeFromParentAsync();
    }

    private async Task EnsureActorMaterializedAsync(string actorId, CancellationToken ct = default)
    {
        var manifestStore = _services.GetService<IAgentManifestStore>();
        if (manifestStore == null)
            return;

        var manifest = await manifestStore.LoadAsync(actorId, ct);
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.AgentTypeName))
            return;

        var agentType = ResolveAgentType(manifest.AgentTypeName);
        if (agentType == null)
        {
            _logger.LogWarning("Failed to resolve agent type {Type} for actor {ActorId}.", manifest.AgentTypeName, actorId);
            return;
        }

        try
        {
            await CreateAsync(agentType, actorId, ct);
        }
        catch (InvalidOperationException) when (_actors.ContainsKey(actorId))
        {
            // Concurrent materialization can race on first access; existing actor is authoritative.
        }
    }

    private async Task<LocalActor?> GetLocalActorAsync(string id)
    {
        var actor = _actors.GetValueOrDefault(id);
        if (actor != null)
            return actor;

        await EnsureActorMaterializedAsync(id);
        return _actors.GetValueOrDefault(id);
    }

    private async Task<LocalActor> GetRequiredAsync(string id) =>
        await GetLocalActorAsync(id) ?? throw new InvalidOperationException($"Actor {id} does not exist");

    private IAgent CreateAgentInstance(System.Type agentType)
    {
        var instance = ActivatorUtilities.CreateInstance(_services, agentType);
        return instance as IAgent
            ?? throw new InvalidOperationException($"Unable to create {agentType.Name}");
    }

    private static System.Type? ResolveAgentType(string agentTypeName)
    {
        var resolved = System.Type.GetType(agentTypeName, throwOnError: false);
        if (resolved != null)
            return resolved;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            resolved = assembly.GetType(agentTypeName, throwOnError: false);
            if (resolved != null)
                return resolved;
        }

        return null;
    }

    private void InjectDependencies(IAgent agent, IEventPublisher publisher, string actorId, ILogger logger)
    {
        if (agent is not GAgentBase gab) return;
        gab.SetId(actorId);
        gab.EventPublisher = publisher;
        gab.Logger = logger;
        gab.Services = _services;
        gab.ManifestStore = _services.GetService<IAgentManifestStore>();
        if (gab is IEventSourcingFactoryBinding statefulBinding)
            statefulBinding.BindEventSourcingFactory(_services);
    }
}
