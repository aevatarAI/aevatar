// ─────────────────────────────────────────────────────────────
// OrleansActorRuntime - IActorRuntime for Orleans.
// Uses IGrainFactory for Grain access, IStreamProvider for
// event publishing, IAgentManifestStore for agent indexing.
//
// Depends on IGrainFactory (not IClusterClient) so the same
// implementation works in both Client and Silo contexts.
// Application host decides what to inject:
//   - Silo: Orleans auto-registers IGrainFactory
//   - Client: IClusterClient implements IGrainFactory
// ─────────────────────────────────────────────────────────────

using Aevatar.Helpers;
using Aevatar.Orleans.Grains;
using Aevatar.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Orleans.Actors;

/// <summary>
/// Orleans-backed actor runtime. Manages agent lifecycle,
/// topology, and persistence via IGrainFactory + IAgentManifestStore.
/// Works in both Client and Silo contexts (depends on IGrainFactory,
/// not IClusterClient).
/// </summary>
public sealed class OrleansActorRuntime : IActorRuntime
{
    private readonly IGrainFactory _grainFactory;
    private readonly IStreamProvider _streamProvider;
    private readonly IAgentManifestStore _manifestStore;
    private readonly ILogger<OrleansActorRuntime> _logger;

    /// <summary>Creates an Orleans actor runtime.</summary>
    public OrleansActorRuntime(
        IGrainFactory grainFactory,
        IStreamProvider streamProvider,
        IAgentManifestStore manifestStore,
        ILogger<OrleansActorRuntime>? logger = null)
    {
        _grainFactory = grainFactory;
        _streamProvider = streamProvider;
        _manifestStore = manifestStore;
        _logger = logger ?? NullLogger<OrleansActorRuntime>.Instance;
    }

    /// <inheritdoc />
    public Task<IActor> CreateAsync<TAgent>(
        string? id = null, CancellationToken ct = default) where TAgent : IAgent
        => CreateAsync(typeof(TAgent), id, ct);

    /// <inheritdoc />
    public async Task<IActor> CreateAsync(
        Type agentType, string? id = null, CancellationToken ct = default)
    {
        var actorId = id ?? AgentId.New(agentType);
        var grain = _grainFactory.GetGrain<IGAgentGrain>(actorId);
        var typeName = agentType.AssemblyQualifiedName
            ?? throw new ArgumentException($"Cannot resolve AQN for {agentType.Name}");

        await grain.InitializeAgentAsync(typeName);

        // Persist manifest for indexing (GetAllAsync / RestoreAllAsync)
        await _manifestStore.SaveAsync(actorId, new AgentManifest
        {
            AgentId = actorId,
            AgentTypeName = typeName,
        }, ct);

        var stream = _streamProvider.GetStream(actorId);
        _logger.LogInformation("Actor {Id} ({Type}) created via Orleans", actorId, agentType.Name);
        return new OrleansClientActor(actorId, grain, stream);
    }

    /// <inheritdoc />
    public async Task<IActor?> GetAsync(string id)
    {
        var grain = _grainFactory.GetGrain<IGAgentGrain>(id);

        // Orleans virtual actor always returns a reference;
        // must verify initialization to avoid returning stale proxies.
        if (!await grain.IsInitializedAsync())
            return null;

        var stream = _streamProvider.GetStream(id);
        return new OrleansClientActor(id, grain, stream);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string id)
        => await _grainFactory.GetGrain<IGAgentGrain>(id).IsInitializedAsync();

    /// <inheritdoc />
    public async Task LinkAsync(
        string parentId, string childId, CancellationToken ct = default)
    {
        var parentGrain = _grainFactory.GetGrain<IGAgentGrain>(parentId);
        var childGrain = _grainFactory.GetGrain<IGAgentGrain>(childId);
        await parentGrain.AddChildAsync(childId);
        await childGrain.SetParentAsync(parentId);
        _logger.LogInformation("Link: {Parent} → {Child}", parentId, childId);
    }

    /// <inheritdoc />
    public async Task UnlinkAsync(string childId, CancellationToken ct = default)
    {
        var childGrain = _grainFactory.GetGrain<IGAgentGrain>(childId);
        var parentId = await childGrain.GetParentAsync();
        if (parentId != null)
            await _grainFactory.GetGrain<IGAgentGrain>(parentId).RemoveChildAsync(childId);
        await childGrain.ClearParentAsync();
    }

    /// <inheritdoc />
    public async Task DestroyAsync(string id, CancellationToken ct = default)
    {
        var grain = _grainFactory.GetGrain<IGAgentGrain>(id);

        // Unlink parent
        var parentId = await grain.GetParentAsync();
        if (parentId != null)
            await _grainFactory.GetGrain<IGAgentGrain>(parentId).RemoveChildAsync(id);

        // Unlink all children (parallel)
        var children = await grain.GetChildrenAsync();
        await Task.WhenAll(children.Select(childId =>
            _grainFactory.GetGrain<IGAgentGrain>(childId).ClearParentAsync()));

        await grain.ClearParentAsync();
        await grain.DeactivateAsync();

        // Remove manifest (prevents zombie entries in GetAllAsync)
        await _manifestStore.DeleteAsync(id, ct);
        _logger.LogInformation("Actor {Id} destroyed via Orleans", id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IActor>> GetAllAsync()
    {
        // Orleans cannot natively enumerate Grains; use manifest index.
        var manifests = await _manifestStore.ListAsync();
        return manifests.Select(m =>
            (IActor)new OrleansClientActor(
                m.AgentId,
                _grainFactory.GetGrain<IGAgentGrain>(m.AgentId),
                _streamProvider.GetStream(m.AgentId)))
            .ToList();
    }

    /// <summary>
    /// Orleans virtual actors activate on demand; explicit restore is not needed.
    /// </summary>
    public Task RestoreAllAsync(CancellationToken ct = default) =>
        Task.CompletedTask;
}
