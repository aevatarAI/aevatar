namespace Aevatar.Workflow.Application.Abstractions.Queries;

public interface IWorkflowCatalogPort
{
    IReadOnlyList<WorkflowCatalogItem> ListWorkflowCatalog();

    WorkflowCatalogItemDetail? GetWorkflowDetail(string workflowName);
}
