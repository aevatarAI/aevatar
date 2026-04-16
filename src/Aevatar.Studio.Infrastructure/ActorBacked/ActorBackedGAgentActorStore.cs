using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Registry;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.ReadModels;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IGAgentActorStore"/>.
/// Reads from the projection document store (CQRS read model).
/// Writes send commands to the Write GAgent.
/// </summary>
internal sealed class ActorBackedGAgentActorStore : IGAgentActorStore
{
    private const string WriteActorIdPrefix = "gagent-registry-";

    private readonly IActorRuntime _runtime;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly IProjectionDocumentReader<GAgentRegistryCurrentStateDocument, string> _documentReader;
    private readonly ILogger<ActorBackedGAgentActorStore> _logger;

    public ActorBackedGAgentActorStore(
        IActorRuntime runtime,
        IAppScopeResolver scopeResolver,
        IProjectionDocumentReader<GAgentRegistryCurrentStateDocument, string> documentReader,
        ILogger<ActorBackedGAgentActorStore> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<GAgentActorGroup>> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveWriteActorId();
        var document = await _documentReader.GetAsync(actorId, cancellationToken);
        if (document?.StateRoot == null ||
            !document.StateRoot.Is(GAgentRegistryState.Descriptor))
            return [];

        var state = document.StateRoot.Unpack<GAgentRegistryState>();
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

    // ── Actor resolution ──

    private string ResolveWriteActorId() => WriteActorIdPrefix + _scopeResolver.ResolveScopeIdOrDefault();

    private async Task<IActor> EnsureWriteActorAsync(CancellationToken ct)
    {
        var actorId = ResolveWriteActorId();
        var actor = await _runtime.GetAsync(actorId);
        return actor ?? await _runtime.CreateAsync<GAgentRegistryGAgent>(actorId, ct);
    }
}
