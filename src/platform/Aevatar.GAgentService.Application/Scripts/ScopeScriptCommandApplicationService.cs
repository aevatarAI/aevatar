using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Application.Scripts;

public sealed class ScopeScriptCommandApplicationService : IScopeScriptCommandPort
{
    private readonly IScriptDefinitionCommandPort _definitionCommandPort;
    private readonly IScriptCatalogCommandPort _catalogCommandPort;
    private readonly ScopeScriptCapabilityOptions _options;

    public ScopeScriptCommandApplicationService(
        IScriptDefinitionCommandPort definitionCommandPort,
        IScriptCatalogCommandPort catalogCommandPort,
        IOptions<ScopeScriptCapabilityOptions> options)
    {
        _definitionCommandPort = definitionCommandPort ?? throw new ArgumentNullException(nameof(definitionCommandPort));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new InvalidOperationException("Scope script capability options are required.");
    }

    public async Task<ScopeScriptUpsertResult> UpsertAsync(
        ScopeScriptUpsertRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedScopeId = ScopeScriptCapabilityOptions.NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var normalizedScriptId = ScopeScriptCapabilityConventions.NormalizeScriptId(request.ScriptId);
        var scriptPackage = request.ScriptPackage?.Clone()
            ?? throw new InvalidOperationException("Script package is required.");
        ScopeScriptCapabilityOptions.NormalizeRequired(
            scriptPackage.GetPrimaryCSharpSource(),
            nameof(request.ScriptPackage));
        var revisionId = ScopeScriptCapabilityConventions.ResolveRevisionId(request.RevisionId);
        var expectedBaseRevision = ScopeScriptCapabilityConventions.ResolveExpectedBaseRevision(request.ExpectedBaseRevision);
        var definitionActorId = _options.BuildDefinitionActorId(normalizedScopeId, normalizedScriptId, revisionId);
        var catalogActorId = _options.BuildCatalogActorId(normalizedScopeId);
        var sourceHash = ScriptPackageModel.ComputePackageHash(scriptPackage);
        var proposalId = BuildProposalId(normalizedScopeId, normalizedScriptId, revisionId);

        // Refactor (iter49/issue-882-script-command-readmodel-activation):
        //   Old pattern: ScopeScriptCommandApplicationService.UpsertAsync explicitly activated definition/catalog readmodels via ActivateAsync before write commands.
        //   New principle: Command service dispatches accepted-only write commands; readmodel activation is owned by scripting committed-state projection activation plan provider.
        var definitionUpsert = await _definitionCommandPort.UpsertDefinitionWithSnapshotAsync(
            normalizedScriptId,
            revisionId,
            scriptPackage,
            definitionActorId,
            normalizedScopeId,
            ct);

        var catalogAccepted = await _catalogCommandPort.PromoteCatalogRevisionAsync(
            catalogActorId,
            normalizedScriptId,
            expectedBaseRevision,
            revisionId,
            definitionUpsert.ActorId,
            sourceHash,
            proposalId,
            normalizedScopeId,
            ct);

        return new ScopeScriptUpsertResult(
            new ScopeScriptAcceptedSummary(
                normalizedScopeId,
                normalizedScriptId,
                catalogActorId,
                definitionUpsert.ActorId,
                revisionId,
                sourceHash,
                ResolveAcceptedAt(catalogAccepted),
                proposalId,
                expectedBaseRevision),
            new ScopeScriptCommandAcceptedHandle(
                definitionUpsert.AcceptedReceipt.ActorId,
                definitionUpsert.AcceptedReceipt.CommandId,
                definitionUpsert.AcceptedReceipt.CorrelationId),
            new ScopeScriptCommandAcceptedHandle(
                catalogAccepted.ActorId,
                catalogAccepted.CommandId,
                catalogAccepted.CorrelationId));
    }

    private static string BuildProposalId(string scopeId, string scriptId, string revisionId) =>
        $"{ScopeScriptCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId))}:{scriptId}:{revisionId}:{Guid.NewGuid():N}";

    private static DateTimeOffset ResolveAcceptedAt(ScriptingCommandAcceptedReceipt receipt) =>
        receipt.AcceptedAt == default
            ? DateTimeOffset.UtcNow
            : receipt.AcceptedAt;

}
