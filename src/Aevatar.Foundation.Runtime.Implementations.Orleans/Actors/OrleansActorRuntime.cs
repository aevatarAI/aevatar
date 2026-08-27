using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

public sealed class OrleansActorRuntime : IActorRuntime
{
    private const int DefaultInitializationAttemptLimit = 30;
    private static readonly TimeSpan DefaultInitializationRetryDelay = TimeSpan.FromSeconds(1);

    private readonly IGrainFactory _grainFactory;
    private readonly Aevatar.Foundation.Abstractions.IStreamProvider _streams;
    private readonly IStreamLifecycleManager _streamLifecycleManager;
    private readonly IActorRuntimeCallbackScheduler _callbackScheduler;
    private readonly ILogger<OrleansActorRuntime> _logger;
    private readonly IAgentKindRegistry _agentKindRegistry;
    private readonly int _initializationAttemptLimit;
    private readonly TimeSpan _initializationRetryDelay;

    public OrleansActorRuntime(
        IGrainFactory grainFactory,
        Aevatar.Foundation.Abstractions.IStreamProvider streams,
        IActorRuntimeCallbackScheduler callbackScheduler,
        IAgentKindRegistry agentKindRegistry,
        IStreamLifecycleManager? streamLifecycleManager = null,
        ILogger<OrleansActorRuntime>? logger = null)
        : this(
            grainFactory,
            streams,
            callbackScheduler,
            agentKindRegistry,
            streamLifecycleManager,
            logger,
            DefaultInitializationAttemptLimit,
            DefaultInitializationRetryDelay)
    {
    }

    internal OrleansActorRuntime(
        IGrainFactory grainFactory,
        Aevatar.Foundation.Abstractions.IStreamProvider streams,
        IActorRuntimeCallbackScheduler callbackScheduler,
        IAgentKindRegistry agentKindRegistry,
        IStreamLifecycleManager? streamLifecycleManager,
        ILogger<OrleansActorRuntime>? logger,
        int initializationAttemptLimit,
        TimeSpan initializationRetryDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initializationAttemptLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(initializationRetryDelay, TimeSpan.Zero);

        _grainFactory = grainFactory;
        _streams = streams;
        _callbackScheduler = callbackScheduler ?? throw new ArgumentNullException(nameof(callbackScheduler));
        _agentKindRegistry = agentKindRegistry ?? throw new ArgumentNullException(nameof(agentKindRegistry));
        _streamLifecycleManager = streamLifecycleManager ?? NullStreamLifecycleManager.Instance;
        _logger = logger ?? NullLogger<OrleansActorRuntime>.Instance;
        _initializationAttemptLimit = initializationAttemptLimit;
        _initializationRetryDelay = initializationRetryDelay;
    }

    public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
        where TAgent : IAgent =>
        CreateAsync(typeof(TAgent), id, ct);

    public async Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
    {
        if (!typeof(IAgent).IsAssignableFrom(agentType))
            throw new InvalidOperationException($"Type {agentType.FullName} does not implement IAgent.");

        if (!_agentKindRegistry.TryGetKindForAgentType(agentType, out var agentKind))
            throw new InvalidOperationException($"Agent type {agentType.FullName} is not registered with a primary [GAgent] kind.");

        var actorId = id ?? $"{agentKind}:{Guid.NewGuid():N}";
        EnsureReservedFleetAuthorityIdentity(actorId, agentKind);
        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(actorId);

        var initialized = await InitializeAgentByKindAsync(
            grain,
            actorId,
            agentKind,
            ct);
        if (!initialized)
            throw new InvalidOperationException($"Failed to initialize Orleans actor {actorId} for kind '{agentKind}'.");

        _logger.LogInformation("Actor {Id} ({Kind}) created via Orleans runtime", actorId, agentKind);
        return new OrleansActor(actorId, grain, _streams);
    }

    /// <summary>Creates actor by stable agent kind through Orleans grain activation.</summary>
    public async Task<IActor> CreateByKindAsync(string agentKind, string? id = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentKind);
        ct.ThrowIfCancellationRequested();

        var actorId = id ?? $"{agentKind.Trim()}:{Guid.NewGuid():N}";
        EnsureReservedFleetAuthorityIdentity(actorId, agentKind.Trim());
        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
        var initialized = await InitializeAgentByKindAsync(
            grain,
            actorId,
            agentKind.Trim(),
            ct);
        if (!initialized)
            throw new InvalidOperationException($"Failed to initialize Orleans actor {actorId} for kind '{agentKind}'.");

        _logger.LogInformation("Actor {Id} ({Kind}) created via Orleans runtime", actorId, agentKind);
        return new OrleansActor(actorId, grain, _streams);
    }

    private async Task<bool> InitializeAgentByKindAsync(
        IRuntimeActorGrain grain,
        string actorId,
        string agentKind,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _initializationAttemptLimit; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await grain.InitializeAgentByKindAsync(agentKind);
            }
            catch (Exception ex) when (
                attempt < _initializationAttemptLimit &&
                IsTopologyConvergenceFailure(ex))
            {
                if (attempt == 1 || attempt % 5 == 0)
                {
                    _logger.LogWarning(
                        ex,
                        "Orleans actor initialization was rejected during topology convergence. " +
                        "actorId={ActorId} actorKind={ActorKind} attempt={Attempt}/{AttemptLimit}",
                        actorId,
                        agentKind,
                        attempt,
                        _initializationAttemptLimit);
                }

                await Task.Delay(_initializationRetryDelay, ct);
            }
        }

        throw new InvalidOperationException("Orleans actor initialization retry loop exited unexpectedly.");
    }

    private static bool IsTopologyConvergenceFailure(Exception exception) =>
        exception switch
        {
            OrleansMessageRejectionException => true,
            OrleansException orleansException when
                orleansException.Message.Contains(
                    "is not stable to perform the lookup",
                    StringComparison.Ordinal) &&
                orleansException.Message.Contains("Retry later", StringComparison.Ordinal) => true,
            AggregateException aggregate when aggregate.InnerExceptions.Count > 0 =>
                aggregate.InnerExceptions.All(IsTopologyConvergenceFailure),
            _ when exception.InnerException is not null =>
                IsTopologyConvergenceFailure(exception.InnerException),
            _ => false,
        };

    public async Task DestroyAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.Equals(
                id,
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The runtime fleet capability authority is runtime-reserved and cannot be destroyed.");
        }

        await _callbackScheduler.PurgeActorAsync(id, ct);
        using var reentrancyScope = RequestContext.AllowCallChainReentrancy();
        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(id);

        var parentId = await grain.GetParentAsync();
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            var parent = _grainFactory.GetGrain<IRuntimeActorGrain>(parentId);
            await parent.RemoveChildAsync(id);
            await _streams.GetStream(parentId).RemoveRelayAsync(id, ct);
            await _streams.GetStream(id).RemoveRelayAsync(parentId, ct);
        }

        var children = await grain.GetChildrenAsync();
        await Task.WhenAll(children.Select(async childId =>
        {
            await _grainFactory.GetGrain<IRuntimeActorGrain>(childId).ClearParentAsync();
            await _streams.GetStream(id).RemoveRelayAsync(childId, ct);
            await _streams.GetStream(childId).RemoveRelayAsync(id, ct);
        }));

        await grain.PurgeAsync();
        await grain.DeactivateAsync();

        _streamLifecycleManager.RemoveStream(id);
        _logger.LogInformation("Actor {Id} destroyed via Orleans runtime", id);
    }

    private static void EnsureReservedFleetAuthorityIdentity(
        string actorId,
        string agentKind)
    {
        var hasReservedId = string.Equals(
            actorId,
            RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            StringComparison.Ordinal);
        var hasReservedKind = string.Equals(
            agentKind,
            RuntimeFleetCapabilityAuthorityIdentity.AgentKind,
            StringComparison.Ordinal);
        if (hasReservedId != hasReservedKind)
        {
            throw new InvalidOperationException(
                $"Runtime fleet authority identity requires the exact actor id/kind pair " +
                $"'{RuntimeFleetCapabilityAuthorityIdentity.ActorId}' / " +
                $"'{RuntimeFleetCapabilityAuthorityIdentity.AgentKind}'.");
        }
    }

    public async Task<IActor?> GetAsync(string id)
    {
        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(id);
        return await grain.IsInitializedAsync() ? new OrleansActor(id, grain, _streams) : null;
    }

    public Task<bool> ExistsAsync(string id)
    {
        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(id);
        return grain.IsInitializedAsync();
    }

    public async Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfFleetAuthorityTopologyEndpoint(parentId, childId);
        var parent = _grainFactory.GetGrain<IRuntimeActorGrain>(parentId);
        var child = _grainFactory.GetGrain<IRuntimeActorGrain>(childId);
        if (!await child.IsInitializedAsync())
            throw new InvalidOperationException($"Child actor {childId} is not initialized.");

        using var reentrancyScope = RequestContext.AllowCallChainReentrancy();
        await parent.AddChildAsync(childId);
        await child.SetParentAsync(parentId);
        await _streams.GetStream(parentId).UpsertRelayAsync(
            StreamForwardingRules.CreateHierarchyBinding(parentId, childId),
            ct);
        // Refactor (iter164/cluster-002-first):
        // Old pattern: workflow code treated presentation completion frames as module triggers.
        // New principle: committed observations are runtime-relayed actor facts.
        // Orleans links install the child-to-parent relay so projection/observation stays unified.
        await _streams.GetStream(childId).UpsertRelayAsync(
            StreamForwardingRules.CreateCommittedObservationBinding(childId, parentId),
            ct);
        _logger.LogInformation("Link: {Parent} -> {Child}", parentId, childId);
    }

    private static void ThrowIfFleetAuthorityTopologyEndpoint(string parentId, string childId)
    {
        if (string.Equals(
                parentId,
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                StringComparison.Ordinal) ||
            string.Equals(
                childId,
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The runtime fleet capability authority cannot participate in actor hierarchy links.");
        }
    }

    public async Task UnlinkAsync(string childId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var reentrancyScope = RequestContext.AllowCallChainReentrancy();
        var child = _grainFactory.GetGrain<IRuntimeActorGrain>(childId);
        var parentId = await child.GetParentAsync();
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            var parent = _grainFactory.GetGrain<IRuntimeActorGrain>(parentId);
            await parent.RemoveChildAsync(childId);
            await _streams.GetStream(parentId).RemoveRelayAsync(childId, ct);
            await _streams.GetStream(childId).RemoveRelayAsync(parentId, ct);
        }

        await child.ClearParentAsync();
    }

    private sealed class NullStreamLifecycleManager : IStreamLifecycleManager
    {
        public static NullStreamLifecycleManager Instance { get; } = new();

        public void RemoveStream(string actorId)
        {
            _ = actorId;
        }
    }
}
