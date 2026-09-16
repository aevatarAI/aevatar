using Aevatar.Studio.Application.Studio.Abstractions;
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
    private readonly IWorkflowCatalogueQueryPort _workflowCatalogueQueryPort;

    public AppScopedWorkflowCatalogueService(IWorkflowCatalogueQueryPort workflowCatalogueQueryPort)
    {
        _workflowCatalogueQueryPort = workflowCatalogueQueryPort ?? throw new ArgumentNullException(nameof(workflowCatalogueQueryPort));
    }

    public Task<ScopeWorkflowCatalogueResponse> QueryAsync(
        ScopeWorkflowCatalogueQuery query,
        CancellationToken ct = default) =>
        _workflowCatalogueQueryPort.QueryAsync(query, ct);
}
