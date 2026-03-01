using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Hosting.Ports;

public sealed class RuntimeScriptPromotionPort : IScriptPromotionPort
{
    private readonly IScriptDefinitionLifecyclePort _definitionLifecyclePort;
    private readonly IScriptCatalogPort _catalogPort;

    public RuntimeScriptPromotionPort(
        IScriptDefinitionLifecyclePort definitionLifecyclePort,
        IScriptCatalogPort catalogPort)
    {
        _definitionLifecyclePort = definitionLifecyclePort;
        _catalogPort = catalogPort;
    }

    public async Task<ScriptPromotionResult> PromoteAsync(
        ScriptPromotionRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var definitionActorId = await _definitionLifecyclePort.UpsertAsync(
            request.ScriptId,
            request.CandidateRevision,
            request.CandidateSource,
            request.CandidateSourceHash,
            string.IsNullOrWhiteSpace(request.DefinitionActorId) ? null : request.DefinitionActorId,
            ct);

        var catalogActorId = string.IsNullOrWhiteSpace(request.CatalogActorId)
            ? "script-catalog"
            : request.CatalogActorId;

        await _catalogPort.PromoteAsync(
            catalogActorId,
            request.ScriptId,
            request.CandidateRevision,
            definitionActorId,
            request.CandidateSourceHash,
            request.ProposalId,
            ct);

        return new ScriptPromotionResult(
            DefinitionActorId: definitionActorId,
            CatalogActorId: catalogActorId,
            PromotedRevision: request.CandidateRevision);
    }

    public Task RollbackAsync(
        ScriptRollbackRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var catalogActorId = string.IsNullOrWhiteSpace(request.CatalogActorId)
            ? "script-catalog"
            : request.CatalogActorId;

        return _catalogPort.RollbackAsync(
            catalogActorId,
            request.ScriptId,
            request.TargetRevision,
            request.Reason,
            request.ProposalId,
            ct);
    }
}
