using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IWorkflowCatalogueQueryPort
{
    Task<ScopeWorkflowCatalogueResponse> QueryAsync(
        ScopeWorkflowCatalogueQuery query,
        CancellationToken ct = default);
}
