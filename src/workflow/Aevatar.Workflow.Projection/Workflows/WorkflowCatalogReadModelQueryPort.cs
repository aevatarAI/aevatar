using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.Workflow.Projection.Workflows;

public sealed class WorkflowCatalogReadModelQueryPort : IWorkflowCatalogPort
{
    private const int CatalogDocumentPageSize = 1000;

    private readonly IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string> _catalogReader;
    private readonly WorkflowCatalogReadModelMapper _mapper;

    public WorkflowCatalogReadModelQueryPort(
        IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string> catalogReader,
        WorkflowCatalogReadModelMapper mapper)
    {
        _catalogReader = catalogReader ?? throw new ArgumentNullException(nameof(catalogReader));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
    }

    // Refactor (iter46/issue-871-workflow-file-catalog-query-port):
    //   Old pattern: Workflow catalog/capabilities query port discovered files, parsed YAML, loaded connector config, and cached results in singleton process memory during query execution.
    //   New principle: WorkflowGAgent per-definition authority; query ports only read freshness-bearing readmodels; file discovery/parsing happens at startup/import time, not in query path.
    public async Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(CancellationToken ct = default)
    {
        var documents = await QueryCatalogDocumentsAsync(ct);
        return documents
            .Select(_mapper.ToCatalogItem)
            .OrderBy(item => item.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SortOrder)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(
        string workflowName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
            return null;

        var document = await _catalogReader.GetAsync(workflowName.Trim(), ct);
        return document == null
            ? null
            : _mapper.ToCatalogItemDetail(document);
    }

    public async Task<IReadOnlyList<WorkflowCatalogItem>> ListPublicWorkflowCatalogAsync(CancellationToken ct = default)
    {
        var catalog = await ListWorkflowCatalogAsync(ct);
        return catalog
            .Where(static item => item.ShowInLibrary)
            .ToList();
    }

    public async Task<WorkflowCatalogItemDetail?> GetPublicWorkflowDetailAsync(
        string templateId,
        CancellationToken ct = default)
    {
        var detail = await GetWorkflowDetailAsync(templateId, ct);
        return detail?.Catalog.ShowInLibrary == true
            ? detail
            : null;
    }

    public async Task<IReadOnlyList<WorkflowCatalogCurrentStateDocument>> QueryCatalogDocumentsAsync(CancellationToken ct)
    {
        var documents = new List<WorkflowCatalogCurrentStateDocument>();
        string? cursor = null;

        do
        {
            var result = await _catalogReader.QueryAsync(new ProjectionDocumentQuery
            {
                Take = CatalogDocumentPageSize,
                Cursor = cursor,
            }, ct);
            documents.AddRange(result.Items);
            cursor = result.NextCursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return documents;
    }
}
