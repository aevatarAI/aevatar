// ─────────────────────────────────────────────────────────────
// LocalActorRuntime - IActorRuntime implementation.
// Create / Get / Destroy / Link / Unlink
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Runtime.Observability;
using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Local.ActivationIndex;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Local.Actors;

/// <summary>Local actor runtime: creates, destroys, links actors, and manages local activation index.</summary>
public sealed class LocalActorRuntime : IActorRuntime
{
    private readonly ConcurrentDictionary<string, LocalActor> _actors = new();
    private readonly IStreamProvider _streams;
    private readonly IStreamLifecycleManager _streamLifecycleManager;
    private readonly ILocalActivationIndexStore _activationIndexStore;
    private readonly IServiceProvider _services;
    private readonly IActorRuntimeCallbackScheduler? _callbackScheduler;
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
        _logger = logger ?? NullLogger<LocalActorRuntime>.Instance;
        _activationIndexStore = services.GetService<ILocalActivationIndexStore>()
            ?? new InMemoryLocalActivationIndexStore();
        _callbackScheduler = services.GetService<IActorRuntimeCallbackScheduler>();
        _deactivationHookDispatcher = services.GetService<IActorDeactivationHookDispatcher>();
    }

    /// <summary>Creates actor for the specified agent type.</summary>
    public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
        CreateAsync(typeof(TAgent), id, ct);

    /// <summary>Creates actor by type and records local activation index.</summary>
    public async Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
    {
        var actorId = id ?? AgentId.New(agentType);
        if (_actors.TryGetValue(actorId, out var existing))
        {
            if (existing.Agent.GetType() != agentType)
            {
                throw new InvalidOperationException(
                    $"Actor '{actorId}' already exists with agent type '{existing.Agent.GetType().FullName}', expected '{agentType.FullName}'.");
            }

            return existing;
        }

        var agent = CreateAgentInstance(agentType);
        var logger = _services.GetService<ILoggerFactory>()?.CreateLogger(agentType.Name) ?? NullLogger.Instance;
        var propagationPolicy = _services.GetService<IEnvelopePropagationPolicy>();
        var deduplicator = _services.GetService<IEventDeduplicator>();
        var actor = new LocalActor(
            agent,
            actorId,
            _streams,
            logger,
            _deactivationHookDispatcher,
            deduplicator);
        var publisher = new LocalActorPublisher(
            actorId,
            () => actor.ParentId,
            () => actor.ChildrenCount,
            _streams,
            propagationPolicy);

        InjectDependencies(agent, publisher, actorId, logger);

        if (!_actors.TryAdd(actorId, actor))
        {
            var authoritative = _actors.GetValueOrDefault(actorId);
            if (authoritative != null)
            {
                if (authoritative.Agent.GetType() != agentType)
                {
                    throw new InvalidOperationException(
                        $"Actor '{actorId}' already exists with agent type '{authoritative.Agent.GetType().FullName}', expected '{agentType.FullName}'.");
                }

                return authoritative;
            }

            throw new InvalidOperationException($"Actor '{actorId}' already exists.");
        }

        var agentTypeName = agentType.AssemblyQualifiedName ?? agentType.FullName ?? agentType.Name;
        using var activity = AevatarActivitySource.StartAgentSpawn(actorId, agentTypeName);
        try
        {
            await _activationIndexStore.UpsertAsync(actorId, agentTypeName, ct);

            await actor.ActivateAsync(ct);
            AgentMetrics.ActiveActors.Add(1);
            AevatarActivitySource.SafeSetStatus(activity, System.Diagnostics.ActivityStatusCode.Ok);
            _logger.LogInformation("Actor {Id} ({Type}) created", actorId, agentType.Name);
            return actor;
        }
        catch (Exception ex)
        {
            AevatarActivitySource.SafeSetStatus(activity, System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>Creates actor by stable agent kind and records local activation index.</summary>
    // Refactor (iter30/cluster-030-workflow-step-raw-actor-lifecycle):
    //   Old pattern: WorkflowStepTargetAgentResolver 用 agent_type/agent_id 通过 Type.GetType + AppDomain scan + IRoleAgentTypeResolver 直接 create/link actors,workflow step parameter 暴露 raw CLR lifecycle
    //   New principle: role-level agent_kind 配合 WorkflowRunGAgent runtime lifecycle;step 只用 target_role;删 agent_type/agent_id raw lifecycle 参数 + IWorkflowAgentTypeAliasProvider;Foundation 加 CreateByKindAsync;Bridge 注册 stable kind token
    public async Task<IActor> CreateByKindAsync(string agentKind, string? id = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentKind);
        var registry = _services.GetRequiredService<IAgentKindRegistry>();
        var implementation = registry.Resolve(agentKind.Trim());
        var actorId = id ?? $"{implementation.Metadata.Kind}:{Guid.NewGuid():N}";

        if (_actors.TryGetValue(actorId, out var existing))
        {
            if (!string.Equals(
                    existing.Agent.GetType().FullName,
                    implementation.Metadata.ImplementationClrTypeName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Actor '{actorId}' already exists with agent type '{existing.Agent.GetType().FullName}', expected kind '{implementation.Metadata.Kind}'.");
            }

            return existing;
        }

        var agent = implementation.Factory(_services);
        var agentType = agent.GetType();
        var logger = _services.GetService<ILoggerFactory>()?.CreateLogger(agentType.Name) ?? NullLogger.Instance;
        var propagationPolicy = _services.GetService<IEnvelopePropagationPolicy>();
        var deduplicator = _services.GetService<IEventDeduplicator>();
        var actor = new LocalActor(
            agent,
            actorId,
            _streams,
            logger,
            _deactivationHookDispatcher,
            deduplicator);
        var publisher = new LocalActorPublisher(
            actorId,
            () => actor.ParentId,
            () => actor.ChildrenCount,
            _streams,
            propagationPolicy);

        InjectDependencies(agent, publisher, actorId, logger);

        if (!_actors.TryAdd(actorId, actor))
        {
            var authoritative = _actors.GetValueOrDefault(actorId);
            if (authoritative != null)
            {
                if (!string.Equals(
                        authoritative.Agent.GetType().FullName,
                        implementation.Metadata.ImplementationClrTypeName,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Actor '{actorId}' already exists with agent type '{authoritative.Agent.GetType().FullName}', expected kind '{implementation.Metadata.Kind}'.");
                }

                return authoritative;
            }

            throw new InvalidOperationException($"Actor '{actorId}' already exists.");
        }

        using var activity = AevatarActivitySource.StartAgentSpawn(actorId, implementation.Metadata.Kind);
        try
        {
            await _activationIndexStore.UpsertAsync(actorId, implementation.Metadata.ImplementationClrTypeName, ct);
            await actor.ActivateAsync(ct);
            AgentMetrics.ActiveActors.Add(1);
            AevatarActivitySource.SafeSetStatus(activity, System.Diagnostics.ActivityStatusCode.Ok);
            _logger.LogInformation("Actor {Id} ({Kind}) created", actorId, implementation.Metadata.Kind);
            return actor;
        }
        catch (Exception ex)
        {
            _actors.TryRemove(actorId, out _);
            await _activationIndexStore.DeleteAsync(actorId, ct);
            AevatarActivitySource.SafeSetStatus(activity, System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>Destroys actor and cleans up stream and activation index.</summary>
    public async Task DestroyAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_callbackScheduler != null)
            await _callbackScheduler.PurgeActorAsync(id, ct);

        if (!_actors.TryRemove(id, out var actor))
        {
            _streamLifecycleManager.RemoveStream(id);
            await _activationIndexStore.DeleteAsync(id, ct);
            return;
        }

        var parentId = await actor.GetParentIdAsync();
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            if (_actors.TryGetValue(parentId, out var parent))
                parent.RemoveChild(id);

            await _streams.GetStream(parentId).RemoveRelayAsync(id, ct);
            await _streams.GetStream(id).RemoveRelayAsync(parentId, ct);
            await actor.UnsubscribeFromParentAsync();
        }

        var children = await actor.GetChildrenIdsAsync();
        foreach (var childId in children)
        {
            if (_actors.TryGetValue(childId, out var child))
                await child.UnsubscribeFromParentAsync();

            await _streams.GetStream(id).RemoveRelayAsync(childId, ct);
            await _streams.GetStream(childId).RemoveRelayAsync(id, ct);
        }

        using var activity = AevatarActivitySource.StartAgentDeactivate(
            id,
            actor.Agent.GetType().AssemblyQualifiedName ?? actor.Agent.GetType().FullName ?? actor.Agent.GetType().Name);
        try
        {
            await actor.DeactivateAsync(ct);
            AevatarActivitySource.SafeSetStatus(activity, System.Diagnostics.ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            AevatarActivitySource.SafeSetStatus(activity, System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            throw;
        }

        AgentMetrics.ActiveActors.Add(-1);
        _streamLifecycleManager.RemoveStream(id);
        await _activationIndexStore.DeleteAsync(id, ct);
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

        var agentTypeName = await _activationIndexStore.GetAgentTypeNameAsync(id);
        if (string.IsNullOrWhiteSpace(agentTypeName))
            return false;

        return ResolveAgentType(agentTypeName) != null;
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
        // Refactor (iter164/cluster-002-first):
        // Old pattern: workflow modules inferred completion from presentation frame descriptors.
        // New principle: committed child state is observed through runtime-owned stream relay.
        // Local runtime registers the same child-to-parent committed observation path as Orleans.
        await _streams.GetStream(childId).UpsertRelayAsync(
            StreamForwardingRules.CreateCommittedObservationBinding(childId, parentId),
            ct);

        using var activity = AevatarActivitySource.StartAgentLink(parentId, childId);
        AevatarActivitySource.SafeSetStatus(activity, System.Diagnostics.ActivityStatusCode.Ok);
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
            await _streams.GetStream(childId).RemoveRelayAsync(parentId, ct);
        }

        await child.UnsubscribeFromParentAsync();

        if (parentId != null)
        {
            using var activity = AevatarActivitySource.StartAgentUnlink(parentId, childId);
            AevatarActivitySource.SafeSetStatus(activity, System.Diagnostics.ActivityStatusCode.Ok);
        }
    }

    private async Task EnsureActorMaterializedAsync(string actorId, CancellationToken ct = default)
    {
        var agentTypeName = await _activationIndexStore.GetAgentTypeNameAsync(actorId, ct);
        if (string.IsNullOrWhiteSpace(agentTypeName))
            return;

        var agentType = ResolveAgentType(agentTypeName);
        if (agentType == null)
        {
            _logger.LogWarning("Failed to resolve agent type {Type} for actor {ActorId}.", agentTypeName, actorId);
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

    private void InjectDependencies(
        IAgent agent,
        LocalActorPublisher publisher,
        string actorId,
        ILogger logger)
    {
        if (agent is not GAgentBase gab) return;
        gab.SetId(actorId);
        gab.EventPublisher = publisher;
        gab.CommittedStateEventPublisher = publisher;
        gab.Logger = logger;
        gab.Services = _services;
        if (gab is IEventSourcingFactoryBinding statefulBinding)
            statefulBinding.BindEventSourcingFactory(_services);
    }
}
