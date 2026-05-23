using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.Workflow.Projection.Workflows;

public sealed class WorkflowCatalogReadModelQueryPort : IWorkflowCatalogPort, IWorkflowCapabilitiesPort
{
    private const string CapabilitiesDocumentId = "workflow-capabilities";
    private readonly IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string> _catalogReader;
    private readonly IProjectionDocumentReader<WorkflowCapabilitiesCurrentStateDocument, string> _capabilitiesReader;
    private readonly WorkflowCatalogReadModelMapper _mapper;

    public WorkflowCatalogReadModelQueryPort(
        IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string> catalogReader,
        IProjectionDocumentReader<WorkflowCapabilitiesCurrentStateDocument, string> capabilitiesReader,
        WorkflowCatalogReadModelMapper mapper)
    {
        _catalogReader = catalogReader ?? throw new ArgumentNullException(nameof(catalogReader));
        _capabilitiesReader = capabilitiesReader ?? throw new ArgumentNullException(nameof(capabilitiesReader));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
    }

    // Refactor (iter46/issue-871-workflow-file-catalog-query-port):
    //   Old pattern: Workflow catalog/capabilities query port discovered files, parsed YAML, loaded connector config, and cached results in singleton process memory during query execution.
    //   New principle: WorkflowGAgent per-definition authority; query ports only read freshness-bearing readmodels; file discovery/parsing happens at startup/import time, not in query path.
    public IReadOnlyList<WorkflowCatalogItem> ListWorkflowCatalog()
    {
        var documents = QueryCatalogDocuments();
        return documents
            .Select(_mapper.ToCatalogItem)
            .OrderBy(item => item.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SortOrder)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public WorkflowCatalogItemDetail? GetWorkflowDetail(string workflowName)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
            return null;

        var document = _catalogReader.GetAsync(workflowName.Trim()).Result;
        return document == null
            ? null
            : _mapper.ToCatalogItemDetail(document);
    }

    public WorkflowCapabilitiesDocument GetCapabilities()
    {
        var capabilities = _capabilitiesReader.GetAsync(CapabilitiesDocumentId).Result
            ?? new WorkflowCapabilitiesCurrentStateDocument
            {
                Id = CapabilitiesDocumentId,
                ActorId = CapabilitiesDocumentId,
                SchemaVersion = "capabilities.v1",
            };
        return _mapper.ToCapabilitiesDocument(capabilities, QueryCatalogDocuments());
    }

    private IReadOnlyList<WorkflowCatalogCurrentStateDocument> QueryCatalogDocuments()
    {
        var result = _catalogReader.QueryAsync(new ProjectionDocumentQuery
        {
            Take = 1000,
        }).Result;
        return result.Items;
    }
}
