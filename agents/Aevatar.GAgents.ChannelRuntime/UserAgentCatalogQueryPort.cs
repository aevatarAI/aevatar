using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Caller-scoped projection-backed query port for user-agent catalog entries.
///
/// All reads filter by the caller's <see cref="OwnerScope"/> using strict full-tuple
/// equality. Stored documents lacking the new <c>owner_scope</c> field are lazily
/// backfilled from the legacy <c>OwnerNyxUserId</c> + <c>Platform</c> fields; the lazy
/// backfill only succeeds for the nyxid surface (cli/web), so legacy lark agents fall
/// through and force deprecate-and-recreate (issue #466 migration plan §3).
///
/// "Not found" and "exists but caller does not own it" both return <c>null</c> — same
/// semantic, no existence disclosure for non-owners. State version reads inherit the
/// same scoping so a non-owner cannot probe version progression either.
/// </summary>
public sealed class UserAgentCatalogQueryPort : IUserAgentCatalogQueryPort
{
    private readonly IProjectionDocumentReader<UserAgentCatalogDocument, string> _documentReader;

    public UserAgentCatalogQueryPort(IProjectionDocumentReader<UserAgentCatalogDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<UserAgentCatalogEntry?> GetForCallerAsync(string agentId, OwnerScope caller, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return null;
        ArgumentNullException.ThrowIfNull(caller);

        var document = await _documentReader.GetAsync(agentId, ct);
        if (document == null || document.Tombstoned) return null;

        return DocumentMatchesCaller(document, caller)
            ? ToEntry(document)
            : null;
    }

    public async Task<IReadOnlyList<UserAgentCatalogEntry>> QueryByCallerAsync(OwnerScope caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        // Take=1000 mirrors the prior unbounded sweep cap. Large enough for any reasonable
        // single-user agent count; the caller filter applies before serialization regardless
        // so a misconfigured tenant cannot probe other tenants' counts via Take.
        var result = await _documentReader.QueryAsync(new ProjectionDocumentQuery { Take = 1000 }, ct);
        return result.Items
            .Where(static doc => !doc.Tombstoned)
            .Where(doc => DocumentMatchesCaller(doc, caller))
            .Select(static doc => ToEntry(doc))
            .ToArray();
    }

    public async Task<long?> GetStateVersionForCallerAsync(string agentId, OwnerScope caller, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return null;
        ArgumentNullException.ThrowIfNull(caller);

        var document = await _documentReader.GetAsync(agentId, ct);
        if (document == null) return null;

        // Tombstoned + non-owned both collapse to null. Tombstoned is null because the
        // wait-for-tombstone-reflected helper relies on null to signal "delete materialized".
        if (document.Tombstoned)
            return null;

        return DocumentMatchesCaller(document, caller) ? document.StateVersion : null;
    }

    /// <summary>
    /// Strict full-tuple equality on the caller's <see cref="OwnerScope"/> against the
    /// document's projected scope. Falls back to lazy backfill from the legacy scattered
    /// fields (OwnerNyxUserId + Platform) when the new owner_scope was not populated;
    /// the backfill only succeeds for the nyxid surface (see <see cref="OwnerScope.FromLegacyFields"/>).
    /// </summary>
    internal static bool DocumentMatchesCaller(UserAgentCatalogDocument document, OwnerScope caller)
    {
        var documentScope = document.OwnerScope ?? OwnerScope.FromLegacyFields(
#pragma warning disable CS0612 // legacy field read for backfill
            document.OwnerNyxUserId,
            document.Platform);
#pragma warning restore CS0612
        return caller.MatchesStrictly(documentScope);
    }

    /// <summary>
    /// Project a stored document onto the public DTO without surfacing the secret
    /// <c>NyxApiKey</c>. The deprecated scattered fields stay populated for ergonomic
    /// access by callers that still pre-date the OwnerScope migration; new code reads
    /// <see cref="UserAgentCatalogEntry.OwnerScope"/> directly.
    /// </summary>
    internal static UserAgentCatalogEntry ToEntry(UserAgentCatalogDocument document)
    {
        var documentScope = document.OwnerScope ?? OwnerScope.FromLegacyFields(
#pragma warning disable CS0612 // legacy field read for backfill
            document.OwnerNyxUserId,
            document.Platform);
        var entry = new UserAgentCatalogEntry
        {
            AgentId = document.Id ?? string.Empty,
            Platform = document.Platform ?? string.Empty,
            ConversationId = document.ConversationId ?? string.Empty,
            NyxProviderSlug = document.NyxProviderSlug ?? string.Empty,
            // NyxApiKey is intentionally NOT surfaced through the public catalog DTO
            // (issue #466 §D). The credential is read through IUserAgentDeliveryTargetReader.
            OwnerNyxUserId = document.OwnerNyxUserId ?? string.Empty,
#pragma warning restore CS0612
            AgentType = document.AgentType ?? string.Empty,
            TemplateName = document.TemplateName ?? string.Empty,
            ScopeId = document.ScopeId ?? string.Empty,
            ApiKeyId = document.ApiKeyId ?? string.Empty,
            ScheduleCron = document.ScheduleCron ?? string.Empty,
            ScheduleTimezone = document.ScheduleTimezone ?? string.Empty,
            Status = document.Status ?? string.Empty,
            LastRunAt = document.LastRunAtUtc,
            NextRunAt = document.NextRunAtUtc,
            ErrorCount = document.ErrorCount,
            LastError = document.LastError ?? string.Empty,
            CreatedAt = document.CreatedAtUtc,
            UpdatedAt = document.UpdatedAtUtc,
            Tombstoned = document.Tombstoned,
            LarkReceiveId = document.LarkReceiveId ?? string.Empty,
            LarkReceiveIdType = document.LarkReceiveIdType ?? string.Empty,
            LarkReceiveIdFallback = document.LarkReceiveIdFallback ?? string.Empty,
            LarkReceiveIdTypeFallback = document.LarkReceiveIdTypeFallback ?? string.Empty,
        };

        if (documentScope is not null)
            entry.OwnerScope = documentScope;

        return entry;
    }
}
