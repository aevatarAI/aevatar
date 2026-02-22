using Aevatar.Foundation.Abstractions.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

public sealed class OrleansActorRuntime : IActorRuntime
{
    private readonly IGrainFactory _grainFactory;
    private readonly IAgentManifestStore _manifestStore;
    private readonly ILogger<OrleansActorRuntime> _logger;

    public OrleansActorRuntime(
        IGrainFactory grainFactory,
        IAgentManifestStore manifestStore,
        ILogger<OrleansActorRuntime>? logger = null)
    {
        _grainFactory = grainFactory;
        _manifestStore = manifestStore;
        _logger = logger ?? NullLogger<OrleansActorRuntime>.Instance;
    }

    public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
        where TAgent : IAgent =>
        CreateAsync(typeof(TAgent), id, ct);

    public async Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
    {
        if (!typeof(IAgent).IsAssignableFrom(agentType))
            throw new InvalidOperationException($"Type {agentType.FullName} does not implement IAgent.");

        var actorId = id ?? AgentId.New(agentType);
        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
        var agentTypeName = agentType.AssemblyQualifiedName
            ?? throw new InvalidOperationException($"Unable to resolve agent type name for {agentType.FullName}.");

        var initialized = await grain.InitializeAgentAsync(agentTypeName);
        if (!initialized)
            throw new InvalidOperationException($"Failed to initialize Orleans actor {actorId}.");

        var manifest = await _manifestStore.LoadAsync(actorId, ct) ?? new AgentManifest { AgentId = actorId };
        manifest.AgentTypeName = agentTypeName;
        await _manifestStore.SaveAsync(actorId, manifest, ct);

        _logger.LogInformation("Actor {Id} ({Type}) created via Orleans runtime", actorId, agentType.Name);
        return new OrleansActor(actorId, grain);
    }

    public async Task DestroyAsync(string id, CancellationToken ct = default)
    {
        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(id);

        var parentId = await grain.GetParentAsync();
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            var parent = _grainFactory.GetGrain<IRuntimeActorGrain>(parentId);
            await parent.RemoveChildAsync(id);
        }

        var children = await grain.GetChildrenAsync();
        await Task.WhenAll(children.Select(childId =>
            _grainFactory.GetGrain<IRuntimeActorGrain>(childId).ClearParentAsync()));

        await grain.ClearParentAsync();
        await grain.DeactivateAsync();

        await _manifestStore.DeleteAsync(id, ct);
        _logger.LogInformation("Actor {Id} destroyed via Orleans runtime", id);
    }

    public async Task<IActor?> GetAsync(string id)
    {
        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(id);
        return await grain.IsInitializedAsync() ? new OrleansActor(id, grain) : null;
    }

    public Task<bool> ExistsAsync(string id)
    {
        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(id);
        return grain.IsInitializedAsync();
    }

    public async Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
    {
        var parent = _grainFactory.GetGrain<IRuntimeActorGrain>(parentId);
        var child = _grainFactory.GetGrain<IRuntimeActorGrain>(childId);

        await parent.AddChildAsync(childId);
        await child.SetParentAsync(parentId);
        _logger.LogInformation("Link: {Parent} -> {Child}", parentId, childId);
    }

    public async Task UnlinkAsync(string childId, CancellationToken ct = default)
    {
        var child = _grainFactory.GetGrain<IRuntimeActorGrain>(childId);
        var parentId = await child.GetParentAsync();
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            var parent = _grainFactory.GetGrain<IRuntimeActorGrain>(parentId);
            await parent.RemoveChildAsync(childId);
        }

        await child.ClearParentAsync();
    }

    public Task RestoreAllAsync(CancellationToken ct = default) =>
        Task.CompletedTask;
}
