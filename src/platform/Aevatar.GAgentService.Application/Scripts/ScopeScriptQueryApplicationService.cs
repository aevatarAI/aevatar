using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Scripting.Core.Ports;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Application.Scripts;

public sealed class ScopeScriptQueryApplicationService : IScopeScriptQueryPort
{
    private readonly IScriptCatalogQueryPort _catalogQueryPort;
    private readonly ScopeScriptCapabilityOptions _options;

    public ScopeScriptQueryApplicationService(
        IScriptCatalogQueryPort catalogQueryPort,
        IOptions<ScopeScriptCapabilityOptions> options)
    {
        _catalogQueryPort = catalogQueryPort ?? throw new ArgumentNullException(nameof(catalogQueryPort));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new InvalidOperationException("Scope script capability options are required.");
    }

    public async Task<IReadOnlyList<ScopeScriptSummary>> ListAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = ScopeScriptCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
        var catalogActorId = _options.BuildCatalogActorId(normalizedScopeId);
        var entries = await _catalogQueryPort.ListCatalogEntriesAsync(catalogActorId, _options.ListTake, ct);

        return entries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.ActiveRevision))
            .OrderByDescending(static entry => entry.UpdatedAtUnixTimeMs)
            .Select(entry => ToSummary(normalizedScopeId, entry))
            .ToArray();
    }

    public async Task<ScopeScriptSummary?> GetByScriptIdAsync(
        string scopeId,
        string scriptId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = ScopeScriptCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedScriptId = ScopeScriptCapabilityConventions.NormalizeScriptId(scriptId);
        var catalogActorId = _options.BuildCatalogActorId(normalizedScopeId);
        var entry = await _catalogQueryPort.GetCatalogEntryAsync(catalogActorId, normalizedScriptId, ct);
        return entry == null || string.IsNullOrWhiteSpace(entry.ActiveRevision)
            ? null
            : ToSummary(normalizedScopeId, entry);
    }

    internal string BuildCatalogActorId(string scopeId) => _options.BuildCatalogActorId(scopeId);

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
