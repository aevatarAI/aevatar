using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Infrastructure.Workflows;

public sealed class RegistryWorkflowDefinitionResolver : IWorkflowDefinitionResolver
{
    private readonly IWorkflowDefinitionCatalog _registry;

    public RegistryWorkflowDefinitionResolver(IWorkflowDefinitionCatalog registry)
    {
        _registry = registry;
    }

    public Task<string?> GetWorkflowYamlAsync(string workflowName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workflowName))
            return Task.FromResult<string?>(null);

        return Task.FromResult(_registry.GetYaml(workflowName.Trim()));
    }
}

