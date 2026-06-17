using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

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
    private readonly ILogger<UserAgentCatalogQueryPort>? _logger;

    public UserAgentCatalogQueryPort(
        IProjectionDocumentReader<UserAgentCatalogDocument, string> documentReader,
        ILogger<UserAgentCatalogQueryPort>? logger = null)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _logger = logger;
    }

    public async Task<UserAgentCatalogReadModelEntry?> GetForCallerAsync(string agentId, OwnerScope caller, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return null;
        ArgumentNullException.ThrowIfNull(caller);

        // Get-by-id is a single-document fetch; equality check happens in-process because
        // the projection reader's Get path doesn't take filters. The push-down is on the
        // QueryByCallerAsync sweep path where it actually matters for scale.
        var document = await _documentReader.GetAsync(agentId, ct);
        if (document == null || document.Tombstoned)
        {
            _logger?.LogInformation(
                "Catalog get-for-caller miss: agentId={AgentId} reason={Reason}",
                agentId,
                document == null ? "document_missing" : "tombstoned");
            return null;
        }

        if (!DocumentMatchesCaller(document, caller))
        {
            var documentScope = document.OwnerScope;
            _logger?.LogWarning(
                "Catalog get-for-caller owner mismatch: agentId={AgentId} " +
                "callerPlatform={CallerPlatform} callerNyxUser={CallerNyxUser} callerScope={CallerScope} callerSender={CallerSender} " +
                "docPlatform={DocPlatform} docNyxUser={DocNyxUser} docScope={DocScope} docSender={DocSender}",
                agentId,
                caller.Platform, caller.NyxUserId, caller.RegistrationScopeId, caller.SenderId,
                documentScope?.Platform, documentScope?.NyxUserId, documentScope?.RegistrationScopeId, documentScope?.SenderId);
            return null;
        }

        return ToEntry(document);
    }

    /// <summary>
    /// Hard cap on the cumulative count returned to a single caller. Bounds the
    /// in-memory accumulation so a misbehaving / pathological tenant cannot drag
    /// unbounded data through the port. 5000 is an order of magnitude above any
    /// reasonable single-user agent count; if a real user ever approaches it, the
    /// product question (page in the UI / cap the catalog UX) outweighs the
    /// aesthetics of "infinite" pagination.
    /// </summary>
    private const int MaxCallerCatalogEntries = 5000;

    public async Task<IReadOnlyList<UserAgentCatalogReadModelEntry>> QueryByCallerAsync(OwnerScope caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        // Push the strict full-tuple OwnerScope equality into the projection store and
        // page through results until the cursor is exhausted. The store applies these
        // filters before paging so the caller's view of the catalog is bounded by
        // ownership at the source — not by an in-process .Where(...) on a sweep that
        // could miss entries past the take boundary, or by a fixed Take=N that
        // truncates the back of the index for users with more than N agents (issue
        // #466 review nit on commit 3b8500e).
        var filters = BuildOwnerScopeFilters(caller);
        var entries = new List<UserAgentCatalogReadModelEntry>();
        string? cursor = null;
        do
        {
            var query = new ProjectionDocumentQuery
            {
                Take = 200,
                Filters = filters,
                Cursor = cursor,
            };

            var page = await _documentReader.QueryAsync(query, ct);
            foreach (var doc in page.Items)
            {
                if (doc.Tombstoned) continue;
                entries.Add(ToEntry(doc));
                if (entries.Count >= MaxCallerCatalogEntries)
                    return entries;
            }

            cursor = page.NextCursor;
        }
        while (!string.IsNullOrEmpty(cursor));

        if (entries.Count == 0)
        {
            _logger?.LogInformation(
                "Catalog query-by-caller returned no entries: platform={Platform} nyxUser={NyxUserId} scope={RegistrationScopeId} sender={SenderId}",
                caller.Platform,
                caller.NyxUserId,
                caller.RegistrationScopeId,
                caller.SenderId);
        }

        return entries;
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
    /// Builds the strict-equality filters that scope a query to the caller's
    /// <see cref="OwnerScope"/> identity, mirroring <see cref="OwnerScope.MatchesStrictly"/>:
    /// nyxid-native callers filter on the full tuple; channel callers filter on the
    /// relay-authenticated <c>(platform, registration_scope_id, sender_id)</c> triple and
    /// deliberately exclude <c>nyx_user_id</c>, which drifts with the credential path that
    /// produced the document (bot-owner relay token vs sender binding token on a shared
    /// bot). The field-path syntax (<c>OwnerScope.NyxUserId</c>) is resolved by both
    /// projection-store providers: InMemory walks the C# property tree; Elasticsearch
    /// resolves via the proto descriptor (proto field name → ES JSON path) and adds the
    /// <c>.keyword</c> suffix for exact-match string fields.
    /// </summary>
    private static IReadOnlyList<ProjectionDocumentFilter> BuildOwnerScopeFilters(OwnerScope caller)
    {
        var filters = new List<ProjectionDocumentFilter>
        {
            new()
            {
                FieldPath = $"{nameof(UserAgentCatalogDocument.OwnerScope)}.{nameof(OwnerScope.Platform)}",
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(caller.Platform),
            },
            new()
            {
                FieldPath = $"{nameof(UserAgentCatalogDocument.OwnerScope)}.{nameof(OwnerScope.RegistrationScopeId)}",
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(caller.RegistrationScopeId),
            },
            new()
            {
                FieldPath = $"{nameof(UserAgentCatalogDocument.OwnerScope)}.{nameof(OwnerScope.SenderId)}",
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(caller.SenderId),
            },
        };

        if (caller.IsNyxIdNative)
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = $"{nameof(UserAgentCatalogDocument.OwnerScope)}.{nameof(OwnerScope.NyxUserId)}",
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(caller.NyxUserId),
            });
        }

        return filters;
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
    internal static UserAgentCatalogReadModelEntry ToEntry(UserAgentCatalogDocument document)
    {
        var documentScope = document.OwnerScope ?? OwnerScope.FromLegacyFields(
#pragma warning disable CS0612 // legacy field read for backfill only
            document.OwnerNyxUserId,
            document.Platform);
#pragma warning restore CS0612
        return new UserAgentCatalogReadModelEntry
        {
            AgentId = document.Id ?? string.Empty,
            ConversationId = document.ConversationId ?? string.Empty,
            NyxProviderSlug = document.NyxProviderSlug ?? string.Empty,
            AgentType = document.AgentType ?? string.Empty,
            TemplateName = document.TemplateName ?? string.Empty,
            ScopeId = document.ScopeId ?? string.Empty,
            ApiKeyId = document.ApiKeyId ?? string.Empty,
            ScheduleCron = document.ScheduleCron ?? string.Empty,
            ScheduleTimezone = document.ScheduleTimezone ?? string.Empty,
            CreatedAt = document.CreatedAtUtc,
            UpdatedAt = document.UpdatedAtUtc,
            Tombstoned = document.Tombstoned,
            LarkReceiveId = document.LarkReceiveId ?? string.Empty,
            LarkReceiveIdType = document.LarkReceiveIdType ?? string.Empty,
            LarkReceiveIdFallback = document.LarkReceiveIdFallback ?? string.Empty,
            LarkReceiveIdTypeFallback = document.LarkReceiveIdTypeFallback ?? string.Empty,
            OutputFormat = document.OutputFormat,
            OwnerScope = documentScope,
            TargetPlatform = document.TargetPlatform ?? string.Empty,
            CatalogAuthorityStateVersion = document.StateVersion,
            CatalogLastEventId = document.LastEventId ?? string.Empty,
        };
    }
}
