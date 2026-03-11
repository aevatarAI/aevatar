using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptCatalogCommandService
    : ScriptActorCommandPortBase<ScriptCatalogGAgent>,
      IScriptCatalogCommandPort
{
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly PromoteScriptRevisionActorRequestAdapter _promoteRevisionAdapter = new();
    private readonly RollbackScriptRevisionActorRequestAdapter _rollbackRevisionAdapter = new();

    public RuntimeScriptCatalogCommandService(
        IActorDispatchPort dispatchPort,
        RuntimeScriptActorAccessor actorAccessor,
        IScriptingActorAddressResolver addressResolver)
        : base(dispatchPort, actorAccessor)
    {
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
    }

    public async Task PromoteCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string expectedBaseRevision,
        string revision,
        string definitionActorId,
        string sourceHash,
        string proposalId,
        CancellationToken ct)
    {
        var resolvedCatalogActorId = await EnsureCatalogActorAsync(catalogActorId, ct);

        await DispatchAsync(
            resolvedCatalogActorId,
            new PromoteScriptRevisionActorRequest(
                ScriptId: scriptId ?? string.Empty,
                Revision: revision ?? string.Empty,
                DefinitionActorId: definitionActorId ?? string.Empty,
                SourceHash: sourceHash ?? string.Empty,
                ProposalId: proposalId ?? string.Empty,
                ExpectedBaseRevision: expectedBaseRevision ?? string.Empty),
            _promoteRevisionAdapter.Map,
            ct);
    }

    public async Task RollbackCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string targetRevision,
        string reason,
        string proposalId,
        string expectedCurrentRevision,
        CancellationToken ct)
    {
        var resolvedCatalogActorId = await EnsureCatalogActorAsync(catalogActorId, ct);

        await DispatchAsync(
            resolvedCatalogActorId,
            new RollbackScriptRevisionActorRequest(
                ScriptId: scriptId ?? string.Empty,
                TargetRevision: targetRevision ?? string.Empty,
                Reason: reason ?? string.Empty,
                ProposalId: proposalId ?? string.Empty,
                ExpectedCurrentRevision: expectedCurrentRevision ?? string.Empty),
            _rollbackRevisionAdapter.Map,
            ct);
    }

    private string ResolveCatalogActorId(string? catalogActorId) =>
        string.IsNullOrWhiteSpace(catalogActorId)
            ? _addressResolver.GetCatalogActorId()
            : catalogActorId;

    private async Task<string> EnsureCatalogActorAsync(
        string? catalogActorId,
        CancellationToken ct)
    {
        var resolvedCatalogActorId = ResolveCatalogActorId(catalogActorId);
        _ = await GetOrCreateActorAsync(
            resolvedCatalogActorId,
            "Script catalog actor not found",
            ct);
        return resolvedCatalogActorId;
    }
}
