using System.Globalization;
using System.Text;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.QueryPorts;

public sealed class ProjectionWorkflowCatalogueQueryPort : IWorkflowCatalogueQueryPort
{
    private const int DefaultTake = 50;
    private const int MaxTake = 100;
    private const int MaxQueryLength = 128;
    private const int SourceReadTake = 10_000;

    private static readonly ScopeWorkflowCatalogueSearchContract SearchContract = new(
        ["name", "description", "workflowId"],
        "Search normalizes text with Unicode FormKC and uses ordinal case-insensitive matching for name and description.",
        "FormKC",
        MaxQueryLength,
        "An omitted, null, empty, or whitespace-only query is equivalent to no search filter.",
        "workflowId participates only by exact match or ordinal case-insensitive prefix match.");

    private readonly IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string> _documentReader;

    public ProjectionWorkflowCatalogueQueryPort(
        IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
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

        var sourceDocuments = await QuerySourceDocumentsAsync(scopeId, ct);
        DateTimeOffset? watermark = sourceDocuments.Count == 0
            ? null
            : sourceDocuments.Max(static document => document.SourceUpdatedAtUtc);

        var rows = sourceDocuments
            .GroupBy(static document => document.WorkflowId, StringComparer.Ordinal)
            .Select(group => BuildRow(scopeId, group.Key, group))
            .Where(row => query.View != ScopeWorkflowCatalogueView.Drafts || row.HasDraftSource)
            .Where(row => Matches(row, normalizedSearch))
            .OrderByDescending(static row => row.UpdatedAtUtc)
            .ThenBy(static row => row.WorkflowId, StringComparer.Ordinal)
            .ToList();

        var page = rows.Skip(offset).Take(take).ToList();
        var nextOffset = offset + page.Count;
        var nextPageToken = nextOffset < rows.Count ? nextOffset.ToString(CultureInfo.InvariantCulture) : null;

        return new ScopeWorkflowCatalogueResponse(
            page,
            nextPageToken,
            new ScopeWorkflowCatalogueFreshness(
                watermark,
                "Refresh watermark is the maximum authoritative source UpdatedAt materialized into the workflow catalogue read model; it is not a synthetic local StateVersion."),
            SearchContract);
    }

    private async Task<IReadOnlyList<ScopeWorkflowCatalogueSourceDocument>> QuerySourceDocumentsAsync(
        string scopeId,
        CancellationToken ct)
    {
        var documents = new List<ScopeWorkflowCatalogueSourceDocument>();
        string? cursor = null;
        do
        {
            var result = await _documentReader.QueryAsync(
                new ProjectionDocumentQuery
                {
                    Take = SourceReadTake,
                    Cursor = cursor,
                    Filters =
                    [
                        new ProjectionDocumentFilter
                        {
                            FieldPath = nameof(ScopeWorkflowCatalogueSourceDocument.ScopeId),
                            Operator = ProjectionDocumentFilterOperator.Eq,
                            Value = ProjectionDocumentValue.FromString(scopeId),
                        },
                    ],
                },
                ct);
            documents.AddRange(result.Items);
            cursor = result.NextCursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return documents;
    }

    private static ScopeWorkflowCatalogueRow BuildRow(
        string scopeId,
        string workflowId,
        IEnumerable<ScopeWorkflowCatalogueSourceDocument> sourceDocuments)
    {
        var sources = sourceDocuments.ToArray();
        var draft = sources
            .Where(static source => IsDraftSource(source.SourceKind))
            .OrderByDescending(static source => source.SourceUpdatedAtUtc)
            .FirstOrDefault();
        var committed = sources
            .Where(static source => IsCommittedSource(source.SourceKind))
            .OrderByDescending(static source => source.SourceUpdatedAtUtc)
            .FirstOrDefault();

        var updatedAtUtc = ResolveUpdatedAt(draft, committed);
        return new ScopeWorkflowCatalogueRow(
            scopeId,
            workflowId,
            ResolveName(workflowId, draft, committed),
            draft?.Description ?? string.Empty,
            draft != null,
            committed != null,
            updatedAtUtc,
            ResolveUpdatedAtSource(draft, committed),
            BuildCapabilities(draft != null, committed != null),
            updatedAtUtc,
            committed == null
                ? null
                : new ScopeWorkflowCatalogueCommittedFacts(
                    committed.ServiceKey,
                    committed.WorkflowName,
                    committed.CommittedActorId,
                    committed.ActiveRevisionId,
                    committed.DeploymentId,
                    committed.DeploymentStatus),
            TeamId: ResolveOptional(committed?.TeamId),
            MemberId: ResolveOptional(committed?.MemberId),
            PublishedServiceId: ResolveOptional(committed?.PublishedServiceId),
            LastBoundRevisionId: ResolveOptional(committed?.LastBoundRevisionId));
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

    private static DateTimeOffset ResolveUpdatedAt(
        ScopeWorkflowCatalogueSourceDocument? draft,
        ScopeWorkflowCatalogueSourceDocument? committed)
    {
        if (draft == null)
            return committed!.SourceUpdatedAtUtc;
        if (committed == null)
            return draft.SourceUpdatedAtUtc;

        return draft.SourceUpdatedAtUtc >= committed.SourceUpdatedAtUtc
            ? draft.SourceUpdatedAtUtc
            : committed.SourceUpdatedAtUtc;
    }

    private static string ResolveUpdatedAtSource(
        ScopeWorkflowCatalogueSourceDocument? draft,
        ScopeWorkflowCatalogueSourceDocument? committed)
    {
        if (draft == null)
            return ScopeWorkflowCatalogueSourceDocument.CommittedSourceKind;
        if (committed == null)
            return ScopeWorkflowCatalogueSourceDocument.DraftSourceKind;

        return draft.SourceUpdatedAtUtc >= committed.SourceUpdatedAtUtc
            ? ScopeWorkflowCatalogueSourceDocument.DraftSourceKind
            : ScopeWorkflowCatalogueSourceDocument.CommittedSourceKind;
    }

    private static string ResolveName(
        string workflowId,
        ScopeWorkflowCatalogueSourceDocument? draft,
        ScopeWorkflowCatalogueSourceDocument? committed)
    {
        if (!string.IsNullOrWhiteSpace(draft?.Name))
            return draft.Name.Trim();
        if (!string.IsNullOrWhiteSpace(committed?.Name))
            return committed.Name.Trim();
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

        if (!int.TryParse(cursor.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0)
            throw new InvalidOperationException("cursor must be a non-negative catalogue offset token returned by the previous response.");

        return offset;
    }

    private static bool IsDraftSource(string sourceKind) =>
        string.Equals(sourceKind, ScopeWorkflowCatalogueSourceDocument.DraftSourceKind, StringComparison.Ordinal);

    private static bool IsCommittedSource(string sourceKind) =>
        string.Equals(sourceKind, ScopeWorkflowCatalogueSourceDocument.CommittedSourceKind, StringComparison.Ordinal);

    private static string? ResolveOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
