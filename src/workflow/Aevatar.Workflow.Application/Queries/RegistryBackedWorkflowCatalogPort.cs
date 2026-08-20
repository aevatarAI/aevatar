using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Application.Queries;

internal sealed class RegistryBackedWorkflowCatalogPort : IWorkflowCatalogPort, IWorkflowCapabilitiesPort
{
    private readonly IWorkflowDefinitionCatalog _workflowRegistry;

    public RegistryBackedWorkflowCatalogPort(IWorkflowDefinitionCatalog workflowRegistry)
    {
        _workflowRegistry = workflowRegistry;
    }

    public Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<WorkflowCatalogItem> catalog = _workflowRegistry.GetNames()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new WorkflowCatalogItem
            {
                Name = name,
                Source = "builtin",
                SourceLabel = "Built-in",
                Group = "starter-workflows",
                GroupLabel = "Starter Workflows",
                ShowInLibrary = true,
            })
            .ToList();
        return Task.FromResult(catalog);
    }

    public Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(
        string workflowName,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workflowName))
            return Task.FromResult<WorkflowCatalogItemDetail?>(null);

        var normalizedName = workflowName.Trim();
        var yaml = _workflowRegistry.GetYaml(normalizedName);
        if (string.IsNullOrWhiteSpace(yaml))
            return Task.FromResult<WorkflowCatalogItemDetail?>(null);

        return Task.FromResult<WorkflowCatalogItemDetail?>(new WorkflowCatalogItemDetail
        {
            Catalog = new WorkflowCatalogItem
            {
                Name = normalizedName,
                Source = "builtin",
                SourceLabel = "Built-in",
                Group = "starter-workflows",
                GroupLabel = "Starter Workflows",
                ShowInLibrary = true,
            },
            Yaml = yaml,
        });
    }

    public Task<IReadOnlyList<WorkflowCatalogItem>> ListPublicWorkflowCatalogAsync(CancellationToken ct = default) =>
        ListWorkflowCatalogAsync(ct);

    public Task<WorkflowCatalogItemDetail?> GetPublicWorkflowDetailAsync(
        string templateId,
        CancellationToken ct = default) =>
        GetWorkflowDetailAsync(templateId, ct);

    public Task<WorkflowCapabilitiesDocument> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new WorkflowCapabilitiesDocument
        {
            SchemaVersion = "capabilities.v1",
            Workflows = _workflowRegistry.GetNames()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new WorkflowCapabilityWorkflow
                {
                    Name = name,
                    Source = "builtin",
                })
                .ToList(),
        });
    }
}
