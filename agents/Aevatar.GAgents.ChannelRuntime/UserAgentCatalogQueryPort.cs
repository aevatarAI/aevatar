using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Caller-scoped projection-backed query port for user-agent catalog entries.
///
/// All reads filter by the caller's <see cref="OwnerScope"/> using strict full-tuple
/// equality, **pushed into the projection store** as four <see cref="ProjectionDocumentFilter"/>
/// Eq predicates (issue #466 §C). No application-layer <c>.Where(...)</c> on un-scoped
/// data: the projection reader returns only documents that already match the caller's
/// scope, so a misconfigured tenant cannot probe counts/cardinality of other tenants
/// via Take.
///
/// Stored documents that pre-date the <c>owner_scope</c> field are re-projected with
/// the new sub-message populated by the projector's lazy backfill from the legacy
/// scattered fields (nyxid surface only — lark legacy is deprecate-and-recreate per
/// migration plan §3). Documents that have never re-projected since this PR shipped
/// remain invisible to caller-scoped queries until the next state event triggers a
/// re-project; that's a transient migration cost, not an authorization gap.
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

        // Get-by-id is a single-document fetch; equality check happens in-process because
        // the projection reader's Get path doesn't take filters. The push-down is on the
        // QueryByCallerAsync sweep path where it actually matters for scale.
        var document = await _documentReader.GetAsync(agentId, ct);
        if (document == null || document.Tombstoned) return null;

        return DocumentMatchesCaller(document, caller)
            ? ToEntry(document)
            : null;
    }

    public async Task<IReadOnlyList<UserAgentCatalogEntry>> QueryByCallerAsync(OwnerScope caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        // Push the strict full-tuple OwnerScope equality into the projection store. The
        // store applies these filters before paging, so the caller's view of the catalog
        // is bounded by ownership at the source — not by an in-process .Where(...) on a
        // sweep that could miss entries past the take boundary or expose other tenants'
        // cardinality through the take ceiling.
        var query = new ProjectionDocumentQuery
        {
            Take = 200,
            Filters = BuildOwnerScopeFilters(caller),
        };

        var result = await _documentReader.QueryAsync(query, ct);
        return result.Items
            .Where(static doc => !doc.Tombstoned)
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
    /// Builds the four strict-equality filters that scope a query to the caller's
    /// <see cref="OwnerScope"/>. The field-path syntax (<c>OwnerScope.NyxUserId</c>) is
    /// resolved by both projection-store providers: InMemory walks the C# property tree;
    /// Elasticsearch resolves via the proto descriptor (proto field name → ES JSON path)
    /// and adds the <c>.keyword</c> suffix for exact-match string fields.
    /// </summary>
    private static IReadOnlyList<ProjectionDocumentFilter> BuildOwnerScopeFilters(OwnerScope caller)
    {
        return new[]
        {
            new ProjectionDocumentFilter
            {
                FieldPath = $"{nameof(UserAgentCatalogDocument.OwnerScope)}.{nameof(OwnerScope.NyxUserId)}",
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(caller.NyxUserId),
            },
            new ProjectionDocumentFilter
            {
                FieldPath = $"{nameof(UserAgentCatalogDocument.OwnerScope)}.{nameof(OwnerScope.Platform)}",
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(caller.Platform),
            },
            new ProjectionDocumentFilter
            {
                FieldPath = $"{nameof(UserAgentCatalogDocument.OwnerScope)}.{nameof(OwnerScope.RegistrationScopeId)}",
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(caller.RegistrationScopeId),
            },
            new ProjectionDocumentFilter
            {
                FieldPath = $"{nameof(UserAgentCatalogDocument.OwnerScope)}.{nameof(OwnerScope.SenderId)}",
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(caller.SenderId),
            },
        };
    }

    /// <summary>
    /// Strict full-tuple equality on the caller's <see cref="OwnerScope"/> against the
    /// document's projected scope. Falls back to lazy backfill from the legacy scattered
    /// fields (OwnerNyxUserId + Platform) when the new owner_scope was not populated;
    /// the backfill only succeeds for the nyxid surface (see <see cref="OwnerScope.FromLegacyFields"/>).
    /// Used by the Get-by-id paths only — the sweep path pushes filters into the store.
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
    /// Project a stored document onto the public DTO. Issue #466 §A: the public surface
    /// carries OwnerScope as the single source of truth; the deprecated scattered fields
    /// (<c>NyxApiKey</c>, <c>OwnerNyxUserId</c>, <c>Platform</c>) are zeroed out so an LLM
    /// or log target serializing the entry sees one ownership field, not three (was: two
    /// after the credential drop). Internal callers that still need the credential go
    /// through <see cref="IUserAgentDeliveryTargetReader"/>.
    /// </summary>
    internal static UserAgentCatalogEntry ToEntry(UserAgentCatalogDocument document)
    {
        var documentScope = document.OwnerScope ?? OwnerScope.FromLegacyFields(
#pragma warning disable CS0612 // legacy field read for backfill only
            document.OwnerNyxUserId,
            document.Platform);
#pragma warning restore CS0612
        var entry = new UserAgentCatalogEntry
        {
            AgentId = document.Id ?? string.Empty,
            // Deprecated proto fields intentionally left at default ("") on the public DTO.
            // OwnerScope is the canonical surface; serializing the entry must not double-
            // expose ownership through both NyxApiKey/OwnerNyxUserId/Platform AND OwnerScope
            // (issue #466 §A: "do not extend the existing fragmented field set").
            ConversationId = document.ConversationId ?? string.Empty,
            NyxProviderSlug = document.NyxProviderSlug ?? string.Empty,
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
