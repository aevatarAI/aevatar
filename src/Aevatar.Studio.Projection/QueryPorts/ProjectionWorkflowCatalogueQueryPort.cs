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
    private const int RowReadTake = 10_000;
    private const string DeactivatedDeploymentStatus = "Deactivated";

    private static readonly ScopeWorkflowCatalogueSearchContract SearchContract = new(
        ["name", "description", "workflowId"],
        "Search normalizes text with Unicode FormKC and uses ordinal case-insensitive matching for name and description.",
        "FormKC",
        MaxQueryLength,
        "An omitted, null, empty, or whitespace-only query is equivalent to no search filter.",
        "workflowId participates only by exact match or ordinal case-insensitive prefix match.");

    private readonly IProjectionDocumentReader<ScopeWorkflowCatalogueRowDocument, string> _documentReader;

    public ProjectionWorkflowCatalogueQueryPort(
        IProjectionDocumentReader<ScopeWorkflowCatalogueRowDocument, string> documentReader)
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

        var rowDocuments = await QueryRowDocumentsAsync(scopeId, ct);
        DateTimeOffset? watermark = rowDocuments.Count == 0
            ? null
            : rowDocuments.Max(static document => document.SourceWatermarkUtc);
        var rows = rowDocuments
            .Select(BuildRow)
            .Where(row => MatchesView(row, query.View))
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
                "Refresh watermark is the maximum authoritative source UpdatedAt materialized into the workflow catalogue row read model; it is not a synthetic local StateVersion."),
            SearchContract);
    }

    private async Task<IReadOnlyList<ScopeWorkflowCatalogueRowDocument>> QueryRowDocumentsAsync(
        string scopeId,
        CancellationToken ct)
    {
        var documents = new List<ScopeWorkflowCatalogueRowDocument>();
        string? cursor = null;
        do
        {
            var result = await _documentReader.QueryAsync(
                new ProjectionDocumentQuery
                {
                    Take = RowReadTake,
                    Cursor = cursor,
                    Filters =
                    [
                        new ProjectionDocumentFilter
                        {
                            FieldPath = nameof(ScopeWorkflowCatalogueRowDocument.ScopeId),
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

    private static ScopeWorkflowCatalogueRow BuildRow(ScopeWorkflowCatalogueRowDocument document)
    {
        var updatedAtUtc = document.RowUpdatedAtUtc;
        return new ScopeWorkflowCatalogueRow(
            document.ScopeId,
            document.WorkflowId,
            ResolveName(document),
            document.Description,
            document.HasDraftSource,
            document.HasPublishedSource,
            updatedAtUtc,
            document.UpdatedAtSource,
            BuildCapabilities(document.HasDraftSource, document.HasPublishedSource, IsArchived(document.DeploymentStatus)),
            document.SourceWatermarkUtc,
            document.HasPublishedSource
                ? new ScopeWorkflowCatalogueCommittedFacts(
                    document.ServiceKey,
                    document.WorkflowName,
                    document.CommittedActorId,
                    document.ActiveRevisionId,
                    document.DeploymentId,
                    document.DeploymentStatus,
                    document.ServiceAppId,
                    document.ServiceNamespace)
                : null,
            PublishedServiceId: ResolveOptional(document.PublishedServiceId));
    }

    private static bool MatchesView(
        ScopeWorkflowCatalogueRow row,
        ScopeWorkflowCatalogueView view) =>
        view switch
        {
            ScopeWorkflowCatalogueView.All => !IsArchived(row),
            ScopeWorkflowCatalogueView.Drafts => row.HasDraftSource && !IsArchived(row),
            ScopeWorkflowCatalogueView.Archived => IsArchived(row),
            _ => !IsArchived(row),
        };

    private static bool IsArchived(ScopeWorkflowCatalogueRow row) =>
        row.Committed is { DeploymentStatus: { Length: > 0 } deploymentStatus } &&
        IsArchived(deploymentStatus);

    private static bool IsArchived(string deploymentStatus) =>
        string.Equals(
            deploymentStatus.Trim(),
            DeactivatedDeploymentStatus,
            StringComparison.OrdinalIgnoreCase);

    private static ScopeWorkflowCatalogueRowCapabilities BuildCapabilities(
        bool hasDraftSource,
        bool hasPublishedSource,
        bool isArchived) =>
        new(
            Open: new ScopeWorkflowCatalogueActionCapability(
                hasDraftSource || hasPublishedSource,
                hasDraftSource || hasPublishedSource ? null : "workflow_source_missing"),
            Activity: new ScopeWorkflowCatalogueActionCapability(
                hasPublishedSource,
                hasPublishedSource ? null : "published_service_source_missing"),
            Rename: new ScopeWorkflowCatalogueActionCapability(
                hasDraftSource,
                hasDraftSource ? null : "draft_source_missing"),
            Delete: new ScopeWorkflowCatalogueActionCapability(
                hasDraftSource && !isArchived,
                isArchived ? "workflow_archived" : hasDraftSource ? null : "draft_source_missing"));

    private static string ResolveName(ScopeWorkflowCatalogueRowDocument document)
    {
        if (!string.IsNullOrWhiteSpace(document.Name))
            return document.Name.Trim();
        if (!string.IsNullOrWhiteSpace(document.WorkflowName))
            return document.WorkflowName.Trim();

        return document.WorkflowId;
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

    private static string? ResolveOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
