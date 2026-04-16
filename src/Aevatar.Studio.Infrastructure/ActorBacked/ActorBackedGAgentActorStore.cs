using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Registry;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.Orchestration;
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
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly IProjectionDocumentReader<GAgentRegistryCurrentStateDocument, string> _documentReader;
    private readonly StudioProjectionPort _projectionPort;
    private readonly ILogger<ActorBackedGAgentActorStore> _logger;

    public ActorBackedGAgentActorStore(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IAppScopeResolver scopeResolver,
        IProjectionDocumentReader<GAgentRegistryCurrentStateDocument, string> documentReader,
        StudioProjectionPort projectionPort,
        ILogger<ActorBackedGAgentActorStore> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
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
        await ActorCommandDispatcher.SendAsync(_dispatchPort, actor, new ActorRegisteredEvent
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
        await ActorCommandDispatcher.SendAsync(_dispatchPort, actor, new ActorUnregisteredEvent
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
        var actor = await _runtime.GetAsync(actorId)
                    ?? await _runtime.CreateAsync<GAgentRegistryGAgent>(actorId, ct);
        await _projectionPort.EnsureProjectionAsync(actorId, StudioProjectionKinds.GAgentRegistry, ct);
        return actor;
    }
}
