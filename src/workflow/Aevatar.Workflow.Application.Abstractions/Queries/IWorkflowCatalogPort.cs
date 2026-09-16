namespace Aevatar.Workflow.Application.Abstractions.Queries;

public interface IWorkflowCatalogPort
{
    // Refactor (iter56/cluster-920-workflow-catalog-async-query): old=sync catalog query, new=async end-to-end
    // Catalog and capability query ports expose Task-returning methods so readmodel readers are awaited.
    // HTTP, WebSocket, and tool callers pass cancellation tokens through this single query seam.
    Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(CancellationToken ct = default);

    Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(string workflowName, CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowCatalogItem>> ListPublicWorkflowCatalogAsync(CancellationToken ct = default);

    Task<WorkflowCatalogItemDetail?> GetPublicWorkflowDetailAsync(string templateId, CancellationToken ct = default);
}
