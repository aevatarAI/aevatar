using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Scripting.Core.Ports;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Application.Scripts;

public sealed class ScopeScriptCommandApplicationService : IScopeScriptCommandPort
{
    private readonly IScriptDefinitionCommandPort _definitionCommandPort;
    private readonly IScriptCatalogCommandPort _catalogCommandPort;
    private readonly IScopeScriptQueryPort _scopeScriptQueryPort;
    private readonly ScopeScriptCapabilityOptions _options;

    public ScopeScriptCommandApplicationService(
        IScriptDefinitionCommandPort definitionCommandPort,
        IScriptCatalogCommandPort catalogCommandPort,
        IScopeScriptQueryPort scopeScriptQueryPort,
        IOptions<ScopeScriptCapabilityOptions> options)
    {
        _definitionCommandPort = definitionCommandPort ?? throw new ArgumentNullException(nameof(definitionCommandPort));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
        _scopeScriptQueryPort = scopeScriptQueryPort ?? throw new ArgumentNullException(nameof(scopeScriptQueryPort));
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
        var sourceText = ScopeScriptCapabilityOptions.NormalizeRequired(request.SourceText, nameof(request.SourceText));
        var revisionId = ScopeScriptCapabilityConventions.ResolveRevisionId(request.RevisionId);
        var expectedBaseRevision = ScopeScriptCapabilityConventions.ResolveExpectedBaseRevision(request.ExpectedBaseRevision);
        var definitionActorId = _options.BuildDefinitionActorId(normalizedScopeId, normalizedScriptId, revisionId);
        var catalogActorId = _options.BuildCatalogActorId(normalizedScopeId);
        var sourceHash = ComputeSha256(sourceText);

        var definitionUpsert = await _definitionCommandPort.UpsertDefinitionWithSnapshotAsync(
            normalizedScriptId,
            revisionId,
            sourceText,
            sourceHash,
            definitionActorId,
            normalizedScopeId,
            ct);

        await _catalogCommandPort.PromoteCatalogRevisionAsync(
            catalogActorId,
            normalizedScriptId,
            expectedBaseRevision,
            revisionId,
            definitionUpsert.ActorId,
            sourceHash,
            BuildProposalId(normalizedScopeId, normalizedScriptId, revisionId),
            normalizedScopeId,
            ct);

        var script =
            await _scopeScriptQueryPort.GetByScriptIdAsync(normalizedScopeId, normalizedScriptId, ct) ??
            new ScopeScriptSummary(
                normalizedScopeId,
                normalizedScriptId,
                catalogActorId,
                definitionUpsert.ActorId,
                revisionId,
                sourceHash,
                DateTimeOffset.UtcNow);

        return new ScopeScriptUpsertResult(
            script,
            revisionId,
            catalogActorId,
            definitionUpsert.ActorId);
    }

    private static string BuildProposalId(string scopeId, string scriptId, string revisionId) =>
        $"{ScopeScriptCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId))}:{scriptId}:{revisionId}";

    private static string ComputeSha256(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
