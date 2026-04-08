using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.Registry;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IGAgentActorStore"/>.
/// Per-scope isolation: each scope gets its own <c>gagent-registry-{scopeId}</c> actor.
/// </summary>
internal sealed class ActorBackedGAgentActorStore : IGAgentActorStore, IAsyncDisposable
{
    private const string ActorIdPrefix = "gagent-registry-";

    private readonly IActorRuntime _runtime;
    private readonly IActorEventSubscriptionProvider _subscriptions;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly ILogger<ActorBackedGAgentActorStore> _logger;

    private readonly ConcurrentDictionary<string, ScopeState> _scopes = new(StringComparer.Ordinal);

    public ActorBackedGAgentActorStore(
        IActorRuntime runtime,
        IActorEventSubscriptionProvider subscriptions,
        IAppScopeResolver scopeResolver,
        ILogger<ActorBackedGAgentActorStore> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<GAgentActorGroup>> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var scopeState = await EnsureScopeAsync(cancellationToken);

        var state = scopeState.Snapshot;
        if (state is null)
            return [];

        return state.Groups
            .Select(g => new GAgentActorGroup(
                g.GagentType,
                g.ActorIds.ToList().AsReadOnly()))
            .ToList()
            .AsReadOnly();
    }

    public async Task AddActorAsync(
        string gagentType, string actorId,
        CancellationToken cancellationToken = default)
    {
        var scopeState = await EnsureScopeAsync(cancellationToken);
        var actor = await EnsureActorAsync(scopeState.ActorId, cancellationToken);
        var evt = new ActorRegisteredEvent
        {
            GagentType = gagentType,
            ActorId = actorId,
        };
        await SendCommandAsync(actor, evt, cancellationToken);
    }

    public async Task RemoveActorAsync(
        string gagentType, string actorId,
        CancellationToken cancellationToken = default)
    {
        var scopeState = await EnsureScopeAsync(cancellationToken);
        var actor = await EnsureActorAsync(scopeState.ActorId, cancellationToken);
        var evt = new ActorUnregisteredEvent
        {
            GagentType = gagentType,
            ActorId = actorId,
        };
        await SendCommandAsync(actor, evt, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes.Values)
        {
            if (scope.Subscription is not null)
                await scope.Subscription.DisposeAsync();
        }
    }

    private string ResolveScopeId()
    {
        var scope = _scopeResolver.Resolve();
        return scope?.ScopeId ?? "default";
    }

    private async Task<ScopeState> EnsureScopeAsync(CancellationToken ct)
    {
        var scopeId = ResolveScopeId();
        var actorId = ActorIdPrefix + scopeId;

        var scopeState = _scopes.GetOrAdd(actorId, _ => new ScopeState(actorId));

        if (scopeState.Initialized)
            return scopeState;

        await scopeState.InitLock.WaitAsync(ct);
        try
        {
            if (scopeState.Initialized)
                return scopeState;

            scopeState.Subscription = await _subscriptions.SubscribeAsync<EventEnvelope>(
                actorId,
                envelope => HandleRegistryEventAsync(actorId, envelope),
                ct);

            await EnsureActorAsync(actorId, ct);
            scopeState.Initialized = true;
        }
        finally
        {
            scopeState.InitLock.Release();
        }

        return scopeState;
    }

    private Task HandleRegistryEventAsync(string actorId, EventEnvelope envelope)
    {
        if (envelope.Payload is null)
            return Task.CompletedTask;

        if (envelope.Payload.Is(GAgentRegistryStateSnapshotEvent.Descriptor))
        {
            var snapshot = envelope.Payload.Unpack<GAgentRegistryStateSnapshotEvent>();
            if (_scopes.TryGetValue(actorId, out var scopeState))
            {
                scopeState.Snapshot = snapshot.Snapshot;
                _logger.LogDebug("Registry readmodel updated for {ActorId}: {GroupCount} groups",
                    actorId, snapshot.Snapshot?.Groups.Count ?? 0);
            }
        }

        return Task.CompletedTask;
    }

    private async Task<IActor> EnsureActorAsync(string actorId, CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(actorId);
        if (actor is not null)
            return actor;

        return await _runtime.CreateAsync<GAgentRegistryGAgent>(actorId, ct);
    }

    private static async Task SendCommandAsync(IActor actor, IMessage command, CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };
        await actor.HandleEventAsync(envelope, ct);
    }

    private sealed class ScopeState(string actorId)
    {
        public string ActorId { get; } = actorId;
        public SemaphoreSlim InitLock { get; } = new(1, 1);
        public volatile bool Initialized;
        public volatile GAgentRegistryState? Snapshot;
        public IAsyncDisposable? Subscription;
    }
}
