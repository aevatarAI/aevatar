using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.Registry;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IGAgentActorStore"/>.
/// Completely stateless: no fields hold snapshot or subscription state.
/// Reads use per-request temporary subscription to the ReadModel GAgent.
/// Writes send commands to the Write GAgent.
/// </summary>
internal sealed class ActorBackedGAgentActorStore : IGAgentActorStore
{
    private const string WriteActorIdPrefix = "gagent-registry-";

    private readonly IActorRuntime _runtime;
    private readonly IActorEventSubscriptionProvider _subscriptions;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly ILogger<ActorBackedGAgentActorStore> _logger;

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
        var state = await ReadFromReadModelAsync(cancellationToken);
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
        var actor = await EnsureWriteActorAsync(cancellationToken);
        await ActorCommandDispatcher.SendAsync(actor, new ActorRegisteredEvent
        {
            GagentType = gagentType,
            ActorId = actorId,
        }, cancellationToken);
    }

    public async Task RemoveActorAsync(
        string gagentType, string actorId,
        CancellationToken cancellationToken = default)
    {
        var actor = await EnsureWriteActorAsync(cancellationToken);
        await ActorCommandDispatcher.SendAsync(actor, new ActorUnregisteredEvent
        {
            GagentType = gagentType,
            ActorId = actorId,
        }, cancellationToken);
    }

    // ── Per-request readmodel read (no service-level state) ──

    private Task<GAgentRegistryState?> ReadFromReadModelAsync(CancellationToken ct)
    {
        return ReadModelSnapshotReader.ReadAsync<GAgentRegistryState, GAgentRegistryStateSnapshotEvent>(
            _subscriptions,
            _runtime,
            ResolveReadModelActorId(),
            typeof(GAgentRegistryReadModelGAgent),
            GAgentRegistryStateSnapshotEvent.Descriptor,
            evt => evt.Snapshot,
            _logger,
            ct);
    }

    // ── Actor resolution ──

    private string ResolveWriteActorId() => WriteActorIdPrefix + _scopeResolver.ResolveScopeIdOrDefault();
    private string ResolveReadModelActorId() => ResolveWriteActorId() + "-readmodel";

    private async Task<IActor> EnsureWriteActorAsync(CancellationToken ct)
    {
        var actorId = ResolveWriteActorId();
        var actor = await _runtime.GetAsync(actorId);
        return actor ?? await _runtime.CreateAsync<GAgentRegistryGAgent>(actorId, ct);
    }
}
