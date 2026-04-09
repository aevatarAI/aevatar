using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Registry;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IGAgentActorStore"/>.
/// Reads the write actor's state directly.
/// Writes send commands to the Write GAgent.
/// </summary>
internal sealed class ActorBackedGAgentActorStore : IGAgentActorStore
{
    private const string WriteActorIdPrefix = "gagent-registry-";

    private readonly IActorRuntime _runtime;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly ILogger<ActorBackedGAgentActorStore> _logger;

    public ActorBackedGAgentActorStore(
        IActorRuntime runtime,
        IAppScopeResolver scopeResolver,
        ILogger<ActorBackedGAgentActorStore> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<GAgentActorGroup>> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await ReadWriteActorStateAsync(cancellationToken);
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

    // ── Read write actor state directly ──

    private async Task<GAgentRegistryState?> ReadWriteActorStateAsync(CancellationToken ct)
    {
        var actorId = ResolveWriteActorId();
        var actor = await _runtime.GetAsync(actorId);
        return (actor?.Agent as IAgent<GAgentRegistryState>)?.State;
    }

    // ── Actor resolution ──

    private string ResolveWriteActorId() => WriteActorIdPrefix + _scopeResolver.ResolveScopeIdOrDefault();

    private async Task<IActor> EnsureWriteActorAsync(CancellationToken ct)
    {
        var actorId = ResolveWriteActorId();
        var actor = await _runtime.GetAsync(actorId);
        return actor ?? await _runtime.CreateAsync<GAgentRegistryGAgent>(actorId, ct);
    }
}
