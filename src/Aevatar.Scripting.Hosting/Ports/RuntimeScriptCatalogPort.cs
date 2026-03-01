using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Hosting.Ports;

public sealed class RuntimeScriptCatalogPort : IScriptCatalogPort
{
    private readonly IActorRuntime _runtime;
    private readonly PromoteScriptRevisionCommandAdapter _promoteAdapter = new();
    private readonly RollbackScriptRevisionCommandAdapter _rollbackAdapter = new();

    public RuntimeScriptCatalogPort(IActorRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task PromoteAsync(
        string catalogActorId,
        string scriptId,
        string revision,
        string definitionActorId,
        string sourceHash,
        string proposalId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogActorId);

        var actor = await GetOrCreateCatalogActorAsync(catalogActorId, ct);
        await actor.HandleEventAsync(
            _promoteAdapter.Map(
                new PromoteScriptRevisionCommand(
                    ScriptId: scriptId ?? string.Empty,
                    Revision: revision ?? string.Empty,
                    DefinitionActorId: definitionActorId ?? string.Empty,
                    SourceHash: sourceHash ?? string.Empty,
                    ProposalId: proposalId ?? string.Empty),
                catalogActorId),
            ct);
    }

    public async Task RollbackAsync(
        string catalogActorId,
        string scriptId,
        string targetRevision,
        string reason,
        string proposalId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogActorId);

        var actor = await GetOrCreateCatalogActorAsync(catalogActorId, ct);
        await actor.HandleEventAsync(
            _rollbackAdapter.Map(
                new RollbackScriptRevisionCommand(
                    ScriptId: scriptId ?? string.Empty,
                    TargetRevision: targetRevision ?? string.Empty,
                    Reason: reason ?? string.Empty,
                    ProposalId: proposalId ?? string.Empty),
                catalogActorId),
            ct);
    }

    public async Task<ScriptCatalogEntrySnapshot?> GetEntryAsync(
        string catalogActorId,
        string scriptId,
        CancellationToken ct)
    {
        _ = ct;

        if (string.IsNullOrWhiteSpace(catalogActorId) || string.IsNullOrWhiteSpace(scriptId))
            return null;

        var actor = await _runtime.GetAsync(catalogActorId);
        if (actor?.Agent is not IScriptCatalogSnapshotSource source)
            return null;

        return source.GetEntry(scriptId);
    }

    private async Task<IActor> GetOrCreateCatalogActorAsync(string catalogActorId, CancellationToken ct)
    {
        if (await _runtime.ExistsAsync(catalogActorId))
        {
            return await _runtime.GetAsync(catalogActorId)
                ?? throw new InvalidOperationException($"Script catalog actor not found: {catalogActorId}");
        }

        return await _runtime.CreateAsync<ScriptCatalogGAgent>(catalogActorId, ct);
    }
}
