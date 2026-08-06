using System.Text;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application;

public interface IAppScopedWorkflowCatalogueService
{
    Task<ScopeWorkflowCatalogueResponse> QueryAsync(
        ScopeWorkflowCatalogueQuery query,
        CancellationToken ct = default);
}

public sealed class AppScopedWorkflowCatalogueService : IAppScopedWorkflowCatalogueService
{
    private const int DefaultTake = 50;
    private const int MaxTake = 100;
    private const int MaxQueryLength = 128;

    private static readonly ScopeWorkflowCatalogueSearchContract SearchContract = new(
        ["name", "description", "workflowId"],
        "Search normalizes text with Unicode FormKC and uses ordinal case-insensitive matching for name and description.",
        "FormKC",
        MaxQueryLength,
        "An omitted, null, empty, or whitespace-only query is equivalent to no search filter.",
        "workflowId participates only by exact match or ordinal case-insensitive prefix match.");

    private readonly AppScopedWorkflowService _draftWorkflowService;
    private readonly IScopeWorkflowCatalogueCommittedSourcePort _committedWorkflowSourcePort;

    public AppScopedWorkflowCatalogueService(
        AppScopedWorkflowService draftWorkflowService,
        IScopeWorkflowCatalogueCommittedSourcePort committedWorkflowSourcePort)
    {
        _draftWorkflowService = draftWorkflowService ?? throw new ArgumentNullException(nameof(draftWorkflowService));
        _committedWorkflowSourcePort = committedWorkflowSourcePort ?? throw new ArgumentNullException(nameof(committedWorkflowSourcePort));
    }

    public async Task<ScopeWorkflowCatalogueResponse> QueryAsync(
        ScopeWorkflowCatalogueQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var scopeId = NormalizeRequired(query.ScopeId, nameof(query.ScopeId));
        var normalizedSearch = NormalizeSearchQuery(query.Query);
        var offset = ParseCursor(query.Cursor);
        var take = NormalizeTake(query.Take);

        var drafts = await _draftWorkflowService.ListDraftsAsync(scopeId, ct);
        var committedWorkflows = await _committedWorkflowSourcePort.ListCatalogueAsync(scopeId, ct);
        var sourceRows = BuildRows(scopeId, drafts, committedWorkflows);
        DateTimeOffset? watermark = sourceRows.Count == 0 ? null : sourceRows.Max(static row => row.SourceWatermarkUtc);

        var rows = sourceRows
            .Where(row => query.View != ScopeWorkflowCatalogueView.Drafts || row.HasDraftSource)
            .Where(row => Matches(row, normalizedSearch))
            .OrderByDescending(static row => row.UpdatedAtUtc)
            .ThenBy(static row => row.WorkflowId, StringComparer.Ordinal)
            .ToList();

        var page = rows.Skip(offset).Take(take).ToList();
        var nextOffset = offset + page.Count;
        var nextPageToken = nextOffset < rows.Count ? nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        return new ScopeWorkflowCatalogueResponse(
            page,
            nextPageToken,
            new ScopeWorkflowCatalogueFreshness(
                watermark,
                "Refresh watermark is the maximum source UpdatedAt observed across the materialized draft workspace and committed workflow read models used by this query; it is not a synthetic state version."),
            SearchContract);
    }

    private static IReadOnlyList<ScopeWorkflowCatalogueRow> BuildRows(
        string scopeId,
        IReadOnlyList<WorkflowDraftSummary> drafts,
        IReadOnlyList<ScopeWorkflowSummary> committedWorkflows)
    {
        var draftByWorkflowId = drafts
            .GroupBy(static draft => draft.WorkflowId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(static draft => draft.UpdatedAtUtc).First(),
                StringComparer.Ordinal);
        var committedByWorkflowId = committedWorkflows
            .GroupBy(static workflow => workflow.WorkflowId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(static workflow => workflow.UpdatedAt).First(),
                StringComparer.Ordinal);

        var workflowIds = new SortedSet<string>(draftByWorkflowId.Keys, StringComparer.Ordinal);
        workflowIds.UnionWith(committedByWorkflowId.Keys);

        var rows = new List<ScopeWorkflowCatalogueRow>(workflowIds.Count);
        foreach (var workflowId in workflowIds)
        {
            draftByWorkflowId.TryGetValue(workflowId, out var draft);
            committedByWorkflowId.TryGetValue(workflowId, out var committed);
            rows.Add(BuildRow(scopeId, workflowId, draft, committed));
        }

        return rows;
    }

    private static ScopeWorkflowCatalogueRow BuildRow(
        string scopeId,
        string workflowId,
        WorkflowDraftSummary? draft,
        ScopeWorkflowSummary? committed)
    {
        var hasDraftSource = draft != null;
        var hasCommittedSource = committed != null;
        var updatedAtUtc = ResolveUpdatedAt(draft, committed);
        var updatedAtSource = ResolveUpdatedAtSource(draft, committed);
        var name = ResolveName(workflowId, draft, committed);
        var description = draft?.Description ?? string.Empty;

        return new ScopeWorkflowCatalogueRow(
            scopeId,
            workflowId,
            name,
            description,
            hasDraftSource,
            hasCommittedSource,
            updatedAtUtc,
            updatedAtSource,
            BuildCapabilities(hasDraftSource, hasCommittedSource),
            updatedAtUtc,
            committed == null
                ? null
                : new ScopeWorkflowCatalogueCommittedFacts(
                    committed.ServiceKey,
                    committed.WorkflowName,
                    committed.ActorId,
                    committed.ActiveRevisionId,
                    committed.DeploymentId,
                    committed.DeploymentStatus));
    }

    private static ScopeWorkflowCatalogueRowCapabilities BuildCapabilities(bool hasDraftSource, bool hasCommittedSource) =>
        new(
            Open: new ScopeWorkflowCatalogueActionCapability(
                hasDraftSource || hasCommittedSource,
                hasDraftSource || hasCommittedSource ? null : "workflow_source_missing"),
            Activity: new ScopeWorkflowCatalogueActionCapability(
                hasCommittedSource,
                hasCommittedSource ? null : "committed_source_missing"),
            Rename: new ScopeWorkflowCatalogueActionCapability(
                hasDraftSource,
                hasDraftSource ? null : "draft_source_missing"),
            Delete: new ScopeWorkflowCatalogueActionCapability(
                hasDraftSource,
                hasDraftSource ? null : "draft_source_missing"));

    private static DateTimeOffset ResolveUpdatedAt(WorkflowDraftSummary? draft, ScopeWorkflowSummary? committed)
    {
        if (draft == null)
            return committed!.UpdatedAt;
        if (committed == null)
            return draft.UpdatedAtUtc;

        return draft.UpdatedAtUtc >= committed.UpdatedAt ? draft.UpdatedAtUtc : committed.UpdatedAt;
    }

    private static string ResolveUpdatedAtSource(WorkflowDraftSummary? draft, ScopeWorkflowSummary? committed)
    {
        if (draft == null)
            return "committed";
        if (committed == null)
            return "draft";

        return draft.UpdatedAtUtc >= committed.UpdatedAt ? "draft" : "committed";
    }

    private static string ResolveName(string workflowId, WorkflowDraftSummary? draft, ScopeWorkflowSummary? committed)
    {
        if (!string.IsNullOrWhiteSpace(draft?.Name))
            return draft.Name.Trim();
        if (!string.IsNullOrWhiteSpace(committed?.DisplayName))
            return committed.DisplayName.Trim();
        if (!string.IsNullOrWhiteSpace(committed?.WorkflowName))
            return committed.WorkflowName.Trim();

        return workflowId;
    }

    private static bool Matches(ScopeWorkflowCatalogueRow row, string normalizedSearch)
    {
        if (normalizedSearch.Length == 0)
            return true;

        return ContainsNormalized(row.Name, normalizedSearch) ||
               ContainsNormalized(row.Description, normalizedSearch) ||
               MatchesWorkflowId(row.WorkflowId, normalizedSearch);
    }

    private static bool ContainsNormalized(string value, string normalizedSearch) =>
        NormalizeText(value).Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesWorkflowId(string workflowId, string normalizedSearch)
    {
        var normalizedWorkflowId = NormalizeText(workflowId);
        return string.Equals(normalizedWorkflowId, normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
               normalizedWorkflowId.StartsWith(normalizedSearch, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSearchQuery(string? query)
    {
        var normalized = NormalizeText(query ?? string.Empty).Trim();
        if (normalized.Length > MaxQueryLength)
            throw new InvalidOperationException($"query must be {MaxQueryLength} characters or fewer after trimming and normalization.");

        return normalized;
    }

    private static string NormalizeText(string value) => value.Normalize(NormalizationForm.FormKC);

    private static string NormalizeRequired(string value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");

        return normalized;
    }

    private static int NormalizeTake(int take)
    {
        if (take <= 0)
            return DefaultTake;

        return Math.Min(take, MaxTake);
    }

    private static int ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return 0;

        if (!int.TryParse(cursor.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var offset) || offset < 0)
            throw new InvalidOperationException("cursor must be a non-negative catalogue offset token returned by the previous response.");

        return offset;
    }
}
