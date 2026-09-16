using System.Globalization;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Studio.Application.WorkflowTemplates;

public sealed class PublicWorkflowTemplateService
{
    private const int DefaultTake = 50;
    private const int MaximumTake = 100;
    private const string VersionSemantics = "workflow-catalog-authority-state-version";

    private readonly IWorkflowCatalogPort _catalogPort;
    private readonly AppScopedWorkflowService _workflowDraftService;

    public PublicWorkflowTemplateService(
        IWorkflowCatalogPort catalogPort,
        AppScopedWorkflowService workflowDraftService)
    {
        _catalogPort = catalogPort ?? throw new ArgumentNullException(nameof(catalogPort));
        _workflowDraftService = workflowDraftService ?? throw new ArgumentNullException(nameof(workflowDraftService));
    }

    public async Task<PublicWorkflowTemplateListResponse> ListAsync(
        PublicWorkflowTemplateListRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var catalog = await _catalogPort.ListPublicWorkflowCatalogAsync(ct);
        var filtered = ApplyQuery(catalog, request.Query);
        var sorted = ApplySort(filtered, request.Sort);
        var offset = ParseCursor(request.Cursor);
        var take = NormalizeTake(request.Take);
        var page = sorted
            .Skip(offset)
            .Take(take + 1)
            .ToList();
        var hasMore = page.Count > take;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        return new PublicWorkflowTemplateListResponse(
            page.Select(static item => ToSummary(item)).ToList(),
            hasMore ? (offset + take).ToString(CultureInfo.InvariantCulture) : null,
            BuildFreshness(sorted));
    }

    public async Task<PublicWorkflowTemplateDetailResponse?> GetAsync(
        string templateId,
        CancellationToken ct = default)
    {
        var normalizedTemplateId = NormalizeRequired(templateId, nameof(templateId));
        var detail = await _catalogPort.GetPublicWorkflowDetailAsync(normalizedTemplateId, ct);
        return detail == null
            ? null
            : ToDetail(detail);
    }

    public async Task<WorkflowDraftCreateAcceptedResponse> InstantiateAsync(
        string scopeId,
        string templateId,
        WorkflowTemplateInstantiateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.ExpectedAuthorityStateVersion.HasValue)
            throw new InvalidOperationException("expectedAuthorityStateVersion is required.");

        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedTemplateId = NormalizeRequired(templateId, nameof(templateId));
        var detail = await _catalogPort.GetPublicWorkflowDetailAsync(normalizedTemplateId, ct);
        if (detail == null)
            throw new WorkflowTemplateNotFoundException(normalizedTemplateId);

        var actualVersion = detail.Catalog.AuthorityStateVersion;
        if (actualVersion != request.ExpectedAuthorityStateVersion.Value)
        {
            throw new WorkflowTemplateVersionConflictException(
                normalizedTemplateId,
                request.ExpectedAuthorityStateVersion.Value,
                actualVersion);
        }

        var summary = ToSummary(detail);
        var draftName = string.IsNullOrWhiteSpace(summary.DefaultDraftName)
            ? summary.DisplayName
            : summary.DefaultDraftName;
        var draftRequest = new SaveWorkflowDraftRequest(
            AppScopedWorkflowService.BuildScopeDirectoryId(normalizedScopeId),
            draftName,
            FileName: draftName,
            detail.Yaml,
            Layout: null);

        return await _workflowDraftService.CreateDraftAsync(normalizedScopeId, draftRequest, ct);
    }

    private static IEnumerable<WorkflowCatalogItem> ApplyQuery(
        IEnumerable<WorkflowCatalogItem> catalog,
        string? query)
    {
        var normalizedQuery = query?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return catalog;

        return catalog.Where(item =>
            Contains(item.Name, normalizedQuery) ||
            Contains(item.Description, normalizedQuery) ||
            item.RequiredConnectors.Any(connector => Contains(connector, normalizedQuery)));
    }

    private static IReadOnlyList<WorkflowCatalogItem> ApplySort(
        IEnumerable<WorkflowCatalogItem> catalog,
        string? sort)
    {
        var normalizedSort = string.IsNullOrWhiteSpace(sort)
            ? "-updated"
            : sort.Trim();

        return normalizedSort switch
        {
            "updated" => catalog
                .OrderBy(item => item.ProjectionWatermark)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "-updated" => catalog
                .OrderByDescending(item => item.ProjectionWatermark)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "-displayName" => catalog
                .OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => catalog
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private static int NormalizeTake(int? take)
    {
        if (!take.HasValue)
            return DefaultTake;

        return Math.Clamp(take.Value, 1, MaximumTake);
    }

    private static int ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return 0;

        if (int.TryParse(cursor.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var offset) &&
            offset >= 0)
        {
            return offset;
        }

        throw new InvalidOperationException("cursor is invalid.");
    }

    private static bool Contains(string? source, string query) =>
        !string.IsNullOrWhiteSpace(source) &&
        source.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static PublicWorkflowTemplateDetailResponse ToDetail(WorkflowCatalogItemDetail detail)
    {
        var summary = ToSummary(detail);
        return new PublicWorkflowTemplateDetailResponse(
            summary,
            detail.Yaml,
            detail.Definition,
            detail.Edges.ToList(),
            summary.AuthorityStateVersion,
            summary.Freshness);
    }

    private static PublicWorkflowTemplateSummary ToSummary(WorkflowCatalogItemDetail detail) =>
        ToSummary(detail.Catalog, detail.Definition.Steps.Count);

    private static PublicWorkflowTemplateSummary ToSummary(WorkflowCatalogItem item) =>
        ToSummary(item, item.StepCount);

    private static PublicWorkflowTemplateSummary ToSummary(WorkflowCatalogItem item, int stepCount)
    {
        var templateId = item.Name.Trim();
        return new PublicWorkflowTemplateSummary(
            templateId,
            templateId,
            item.Description,
            templateId,
            item.AuthorityStateVersion,
            stepCount,
            item.RequiredConnectors.ToList(),
            item.RequiresLlmProvider,
            new PublicWorkflowTemplateFreshness(
                item.ProjectionWatermark,
                item.LastEventId,
                VersionSemantics));
    }

    private static PublicWorkflowTemplateFreshness BuildFreshness(IReadOnlyList<WorkflowCatalogItem> items)
    {
        var projectionWatermark = items.Count == 0
            ? default
            : items.Max(static item => item.ProjectionWatermark);
        var stateVersion = items.Count == 0
            ? 0
            : items.Max(static item => item.AuthorityStateVersion);
        var lastEventId = items
            .OrderByDescending(static item => item.AuthorityStateVersion)
            .Select(static item => item.LastEventId)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

        return new PublicWorkflowTemplateFreshness(
            projectionWatermark,
            lastEventId,
            $"{VersionSemantics}:max={stateVersion.ToString(CultureInfo.InvariantCulture)}");
    }

    private static string NormalizeRequired(string value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");

        return normalized;
    }
}

public sealed record PublicWorkflowTemplateListRequest(
    string? Query = null,
    string? Sort = null,
    string? Cursor = null,
    int? Take = null);

public sealed record PublicWorkflowTemplateListResponse(
    IReadOnlyList<PublicWorkflowTemplateSummary> Items,
    string? NextCursor,
    PublicWorkflowTemplateFreshness Freshness);

public sealed record PublicWorkflowTemplateDetailResponse(
    PublicWorkflowTemplateSummary Template,
    string Yaml,
    WorkflowCatalogDefinition Definition,
    IReadOnlyList<WorkflowCatalogEdge> Edges,
    long AuthorityStateVersion,
    PublicWorkflowTemplateFreshness Freshness);

public sealed record PublicWorkflowTemplateSummary(
    string TemplateId,
    string DisplayName,
    string Description,
    string DefaultDraftName,
    long AuthorityStateVersion,
    int StepCount,
    IReadOnlyList<string> RequiredConnections,
    bool RequiresLlmProvider,
    PublicWorkflowTemplateFreshness Freshness);

public sealed record PublicWorkflowTemplateFreshness(
    DateTimeOffset ProjectionWatermark,
    string LastEventId,
    string VersionSemantics);

public sealed record WorkflowTemplateInstantiateRequest(long? ExpectedAuthorityStateVersion);

public sealed class WorkflowTemplateNotFoundException : InvalidOperationException
{
    public WorkflowTemplateNotFoundException(string templateId)
        : base($"Workflow template '{templateId}' was not found.")
    {
        TemplateId = templateId;
    }

    public string TemplateId { get; }
}

public sealed class WorkflowTemplateVersionConflictException : InvalidOperationException
{
    public WorkflowTemplateVersionConflictException(
        string templateId,
        long expectedAuthorityStateVersion,
        long actualAuthorityStateVersion)
        : base($"Workflow template '{templateId}' authority state version is stale.")
    {
        TemplateId = templateId;
        ExpectedAuthorityStateVersion = expectedAuthorityStateVersion;
        ActualAuthorityStateVersion = actualAuthorityStateVersion;
    }

    public string TemplateId { get; }

    public long ExpectedAuthorityStateVersion { get; }

    public long ActualAuthorityStateVersion { get; }
}
