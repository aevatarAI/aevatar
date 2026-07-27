using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IWorkflowTemplateCatalogQueryPort
{
    Task<WorkflowTemplateCatalogPage> ListAsync(
        WorkflowTemplateCatalogQuery query,
        CancellationToken ct = default);

    Task<WorkflowTemplateLookupResult> GetAsync(
        string templateId,
        string revision,
        CancellationToken ct = default);
}
