using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Scripting.Core.Ports;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Application.Scripts;

public sealed class ScopeScriptSaveObservationService : IScopeScriptSaveObservationPort
{
    private readonly IScriptCatalogQueryPort _catalogQueryPort;
    private readonly ScopeScriptCapabilityOptions _options;

    public ScopeScriptSaveObservationService(
        IScriptCatalogQueryPort catalogQueryPort,
        IOptions<ScopeScriptCapabilityOptions> options)
    {
        _catalogQueryPort = catalogQueryPort ?? throw new ArgumentNullException(nameof(catalogQueryPort));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new InvalidOperationException("Scope script capability options are required.");
    }

    public async Task<ScopeScriptSaveObservationResult> ObserveAsync(
        string scopeId,
        string scriptId,
        ScopeScriptSaveObservationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedScopeId = ScopeScriptCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedScriptId = ScopeScriptCapabilityConventions.NormalizeScriptId(scriptId);
        var normalizedRevisionId = ScopeScriptCapabilityOptions.NormalizeRequired(request.RevisionId, nameof(request.RevisionId));
        var normalizedDefinitionActorId = ScopeScriptCapabilityOptions.NormalizeRequired(request.DefinitionActorId, nameof(request.DefinitionActorId));
        var normalizedSourceHash = ScopeScriptCapabilityOptions.NormalizeRequired(request.SourceHash, nameof(request.SourceHash));
        var normalizedProposalId = ScopeScriptCapabilityConventions.NormalizeOptional(request.ProposalId);
        var normalizedExpectedBaseRevision = ScopeScriptCapabilityConventions.NormalizeOptional(request.ExpectedBaseRevision);

        var catalogActorId = _options.BuildCatalogActorId(normalizedScopeId);
        var entry = await _catalogQueryPort.GetCatalogEntryAsync(catalogActorId, normalizedScriptId, ct);
        var currentScript = entry == null || string.IsNullOrWhiteSpace(entry.ActiveRevision)
            ? null
            : ToSummary(normalizedScopeId, entry);

        if (entry != null &&
            string.Equals(entry.ActiveRevision, normalizedRevisionId, StringComparison.Ordinal))
        {
            if (string.Equals(entry.ActiveDefinitionActorId, normalizedDefinitionActorId, StringComparison.Ordinal) &&
                string.Equals(entry.ActiveSourceHash, normalizedSourceHash, StringComparison.Ordinal) &&
                (string.IsNullOrWhiteSpace(normalizedProposalId) ||
                 string.Equals(entry.LastProposalId, normalizedProposalId, StringComparison.Ordinal)))
            {
                return new ScopeScriptSaveObservationResult(
                    normalizedScopeId,
                    normalizedScriptId,
                    ScopeScriptSaveObservationStatuses.Applied,
                    $"Revision '{normalizedRevisionId}' is now active.",
                    currentScript);
            }

            return new ScopeScriptSaveObservationResult(
                normalizedScopeId,
                normalizedScriptId,
                ScopeScriptSaveObservationStatuses.Rejected,
                $"Save request for revision '{normalizedRevisionId}' was superseded by a different accepted catalog payload for the same revision.",
                currentScript);
        }

        if (entry != null &&
            !string.IsNullOrWhiteSpace(normalizedExpectedBaseRevision) &&
            !string.Equals(entry.ActiveRevision, normalizedExpectedBaseRevision, StringComparison.Ordinal) &&
            !string.Equals(entry.ActiveRevision, normalizedRevisionId, StringComparison.Ordinal))
        {
            return new ScopeScriptSaveObservationResult(
                normalizedScopeId,
                normalizedScriptId,
                ScopeScriptSaveObservationStatuses.Rejected,
                $"Save request could not be applied because script '{normalizedScriptId}' is currently at revision '{entry.ActiveRevision}' instead of expected base revision '{normalizedExpectedBaseRevision}'.",
                currentScript);
        }

        return new ScopeScriptSaveObservationResult(
            normalizedScopeId,
            normalizedScriptId,
            ScopeScriptSaveObservationStatuses.Pending,
            $"Save request for revision '{normalizedRevisionId}' has been accepted and is waiting to appear in the catalog read model.",
            currentScript);
    }

    private static ScopeScriptSummary ToSummary(
        string scopeId,
        ScriptCatalogEntrySnapshot entry) =>
        new(
            scopeId,
            entry.ScriptId,
            entry.CatalogActorId,
            entry.ActiveDefinitionActorId,
            entry.ActiveRevision,
            entry.ActiveSourceHash,
            entry.UpdatedAtUnixTimeMs <= 0
                ? DateTimeOffset.UnixEpoch
                : DateTimeOffset.FromUnixTimeMilliseconds(entry.UpdatedAtUnixTimeMs));
}
