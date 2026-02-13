// ─────────────────────────────────────────────────────────────
// GAgentGrain - core Orleans Grain hosting an IAgent instance.
//
// Design:
// - Non-reentrant: HandleEventAsync runs serially (agent state safety).
// - [AlwaysInterleave] on read-only & hierarchy methods for concurrency.
// - PropagateEventAsync is fire-and-forget to minimize turn duration.
// - IEventDeduplicator prevents duplicate processing (at-least-once).
// - AevatarActivitySource + AgentMetrics for observability.
// ─────────────────────────────────────────────────────────────

using System.Diagnostics;
using Aevatar.Deduplication;
using Aevatar.EventSourcing;
using Aevatar.Observability;
using Aevatar.Orleans.Actor;
using Aevatar.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;

namespace Aevatar.Orleans.Grain;

/// <summary>
/// Core Orleans Grain that hosts an IAgent instance. Not marked
/// [Reentrant] — HandleEventAsync is serialized by Orleans turn-based
/// concurrency, ensuring agent state consistency.
/// </summary>
public class GAgentGrain : Orleans.Grain, IGAgentGrain
{
    private readonly IPersistentState<OrleansAgentState> _state;
    private IAgent? _agent;
    private IStreamProvider _streamProvider = null!;
    private IEventDeduplicator _deduplicator = null!;
    private ILogger _logger = NullLogger.Instance;

    /// <summary>Creates a GAgentGrain with persistent state.</summary>
    public GAgentGrain(
        [PersistentState("agent", Constants.GrainStorageName)]
        IPersistentState<OrleansAgentState> state)
    {
        _state = state;
    }

    /// <summary>Logger exposed for GrainEventPublisher fault logging.</summary>
    internal ILogger Logger => _logger;

    // ═══════════════════════════════════════════════════════
    // Grain Lifecycle
    // ═══════════════════════════════════════════════════════

    /// <inheritdoc />
    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _streamProvider = ServiceProvider.GetRequiredService<IStreamProvider>();
        _deduplicator = ServiceProvider.GetRequiredService<IEventDeduplicator>();
        var loggerFactory = ServiceProvider.GetService<ILoggerFactory>();
        _logger = loggerFactory?.CreateLogger<GAgentGrain>() ?? NullLogger<GAgentGrain>.Instance;

        // Restore agent if previously initialized
        if (!string.IsNullOrEmpty(_state.State.AgentTypeName))
        {
            await InitializeAgentInternalAsync(_state.State.AgentTypeName, ct);
        }
    }

    /// <inheritdoc />
    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        if (_agent != null)
        {
            await _agent.DeactivateAsync(ct);
            AgentMetrics.ActiveActors.Add(-1);
        }
    }

    // ═══════════════════════════════════════════════════════
    // Initialization
    // ═══════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<bool> InitializeAgentAsync(string agentTypeName)
    {
        if (_agent != null) return true; // Already initialized

        if (!await InitializeAgentInternalAsync(agentTypeName))
            return false;

        // Persist metadata for reactivation
        _state.State.AgentTypeName = agentTypeName;
        _state.State.AgentId = this.GetPrimaryKeyString();
        await _state.WriteStateAsync();
        return true;
    }

    /// <inheritdoc />
    public Task<bool> IsInitializedAsync() =>
        Task.FromResult(_agent != null);

    // ═══════════════════════════════════════════════════════
    // Event Handling (sole entry point from MassTransitEventHandler)
    // ═══════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task HandleEventAsync(byte[] envelopeBytes)
    {
        if (_agent == null)
            throw new InvalidOperationException("Grain not initialized");

        var envelope = EventEnvelope.Parser.ParseFrom(envelopeBytes);

        // Deduplication: at-least-once → effectively-once
        if (!await _deduplicator.TryRecordAsync(envelope.Id))
        {
            _logger.LogDebug("Duplicate event {EventId} skipped", envelope.Id);
            return;
        }

        // Loop protection: check publisher chain
        if (envelope.Metadata.TryGetValue(Constants.PublishersMetadataKey, out var pubs)
            && pubs.Contains(this.GetPrimaryKeyString()))
        {
            _logger.LogDebug("Loop detected for event {EventId}, skipping", envelope.Id);
            return;
        }

        // ── Observability ──
        var agentId = this.GetPrimaryKeyString();
        using var activity = AevatarActivitySource.StartHandleEvent(agentId, envelope.Id);
        var sw = Stopwatch.StartNew();

        await _agent.HandleEventAsync(envelope);

        sw.Stop();
        AgentMetrics.EventsHandled.Add(1,
            new KeyValuePair<string, object?>("agent.id", agentId),
            new KeyValuePair<string, object?>("agent.type", _state.State.AgentTypeName));
        AgentMetrics.HandlerDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("agent.id", agentId),
            new KeyValuePair<string, object?>("agent.type", _state.State.AgentTypeName));

        // ── Propagate (fire-and-forget) ──
        _ = PropagateEventAsync(envelope)
            .ContinueWith(
                t => _logger.LogError(t.Exception,
                    "PropagateEventAsync failed for {AgentId}", agentId),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    // ═══════════════════════════════════════════════════════
    // Event Propagation (fire-and-forget, does not block HandleEventAsync)
    // ═══════════════════════════════════════════════════════

    /// <summary>Propagates event to children/parent via MassTransit streams.</summary>
    internal async Task PropagateEventAsync(EventEnvelope envelope)
    {
        // Append self to publisher chain
        var selfId = this.GetPrimaryKeyString();
        var existing = envelope.Metadata.GetValueOrDefault(Constants.PublishersMetadataKey, "");
        envelope.Metadata[Constants.PublishersMetadataKey] =
            string.IsNullOrEmpty(existing) ? selfId : $"{existing},{selfId}";

        // ★ Snapshot hierarchy to prevent race with [AlwaysInterleave] methods
        var children = _state.State.Children.ToList();
        var parentId = _state.State.ParentId;

        switch (envelope.Direction)
        {
            case EventDirection.Self:
                break; // No propagation

            case EventDirection.Down:
                await Task.WhenAll(children.Select(cid => SendToStreamAsync(cid, envelope)));
                break;

            case EventDirection.Up:
                if (parentId != null)
                    await SendToStreamAsync(parentId, envelope);
                break;

            case EventDirection.Both:
                var tasks = children.Select(cid => SendToStreamAsync(cid, envelope)).ToList();
                if (parentId != null)
                    tasks.Add(SendToStreamAsync(parentId, envelope));
                await Task.WhenAll(tasks);
                break;
        }
    }

    private async Task SendToStreamAsync(string targetId, EventEnvelope envelope)
    {
        var stream = _streamProvider.GetStream(targetId);
        await stream.ProduceAsync(envelope);
    }

    // ═══════════════════════════════════════════════════════
    // Hierarchy Management ([AlwaysInterleave] on interface)
    // ═══════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task AddChildAsync(string childId)
    {
        if (!_state.State.Children.Contains(childId))
        {
            _state.State.Children.Add(childId);
            await _state.WriteStateAsync();
        }
    }

    /// <inheritdoc />
    public async Task RemoveChildAsync(string childId)
    {
        if (_state.State.Children.Remove(childId))
            await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public async Task SetParentAsync(string parentId)
    {
        _state.State.ParentId = parentId;
        await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public async Task ClearParentAsync()
    {
        _state.State.ParentId = null;
        await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetChildrenAsync() =>
        Task.FromResult<IReadOnlyList<string>>(_state.State.Children.ToList());

    /// <inheritdoc />
    public Task<string?> GetParentAsync() =>
        Task.FromResult(_state.State.ParentId);

    // ═══════════════════════════════════════════════════════
    // Description & Deactivation
    // ═══════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<string> GetDescriptionAsync() =>
        _agent != null ? await _agent.GetDescriptionAsync() : $"Uninitialized:{this.GetPrimaryKeyString()}";

    /// <inheritdoc />
    public Task DeactivateAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════
    // Internal: Agent Creation & Dependency Injection
    // ═══════════════════════════════════════════════════════

    private async Task<bool> InitializeAgentInternalAsync(
        string typeName, CancellationToken ct = default)
    {
        var agentType = Type.GetType(typeName);
        if (agentType == null)
        {
            _logger.LogError("Cannot resolve agent type: {TypeName}", typeName);
            return false;
        }

        var agent = CreateAgentInstance(agentType);
        InjectDependencies(agent, agentType);

        await agent.ActivateAsync(ct);
        _agent = agent;
        AgentMetrics.ActiveActors.Add(1);
        return true;
    }

    private IAgent CreateAgentInstance(Type agentType)
    {
        // Prefer DI resolution; fallback to Activator
        if (ServiceProvider.GetService(agentType) is IAgent fromDi)
            return fromDi;

        return Activator.CreateInstance(agentType) as IAgent
            ?? throw new InvalidOperationException($"Cannot create {agentType.Name}");
    }

    private void InjectDependencies(IAgent agent, Type agentType)
    {
        if (agent is not GAgentBase gab) return;

        var actorId = this.GetPrimaryKeyString();
        var loggerFactory = ServiceProvider.GetService<ILoggerFactory>();
        var agentLogger = loggerFactory?.CreateLogger(agentType.Name) ?? NullLogger.Instance;

        gab.SetId(actorId);
        gab.EventPublisher = new GrainEventPublisher(actorId, this, _streamProvider, agentLogger);
        gab.Logger = agentLogger;
        gab.Services = ServiceProvider;
        gab.ManifestStore = ServiceProvider.GetService<IAgentManifestStore>();

        InjectStateStore(agent);
        InjectEventSourcingBehavior(agent, actorId);
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
                var store = ServiceProvider.GetService(storeType);
                if (store != null)
                    type.GetProperty("StateStore")?.SetValue(agent, store);
                break;
            }
            type = type.BaseType;
        }
    }

    private void InjectEventSourcingBehavior(IAgent agent, string actorId)
    {
        // Walk the type hierarchy to find GAgentBase<TState>
        var type = agent.GetType();
        while (type != null)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(GAgentBase<>))
            {
                var stateType = type.GetGenericArguments()[0];
                var behaviorType = typeof(IEventSourcingBehavior<>).MakeGenericType(stateType);

                // Check if already registered in DI (custom behavior)
                var behavior = ServiceProvider.GetService(behaviorType);
                if (behavior != null) break; // Already injected via DI

                // Create default EventSourcingBehavior if IEventStore is available
                var eventStore = ServiceProvider.GetService<IEventStore>();
                if (eventStore != null)
                {
                    var implType = typeof(EventSourcingBehavior<>).MakeGenericType(stateType);
                    behavior = Activator.CreateInstance(implType, eventStore, actorId);
                    // Inject into agent if it has a matching property
                    // (This would require a convention or interface extension)
                }
                break;
            }
            type = type.BaseType;
        }
    }
}
