using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Application.Queries;

internal sealed class RegistryBackedWorkflowCatalogPort : IWorkflowCatalogPort, IWorkflowCapabilitiesPort
{
    private readonly IWorkflowDefinitionRegistry _workflowRegistry;

    public RegistryBackedWorkflowCatalogPort(IWorkflowDefinitionRegistry workflowRegistry)
    {
        _workflowRegistry = workflowRegistry;
    }

    public IReadOnlyList<WorkflowCatalogItem> ListWorkflowCatalog()
    {
        return _workflowRegistry.GetNames()
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
    }

    public WorkflowCatalogItemDetail? GetWorkflowDetail(string workflowName)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
            return null;

        var normalizedName = workflowName.Trim();
        var yaml = _workflowRegistry.GetYaml(normalizedName);
        if (string.IsNullOrWhiteSpace(yaml))
            return null;

        return new WorkflowCatalogItemDetail
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
        };
    }

    public WorkflowCapabilitiesDocument GetCapabilities()
    {
        return new WorkflowCapabilitiesDocument
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
        };
    }
}
