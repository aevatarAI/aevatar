using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Infrastructure.Workflows;

public sealed class CatalogWorkflowDefinitionResolver : IWorkflowDefinitionResolver
{
    private readonly IWorkflowDefinitionLookupService _lookup;

    public CatalogWorkflowDefinitionResolver(IWorkflowDefinitionLookupService lookup)
    {
        _lookup = lookup;
    }

    public Task<string?> GetWorkflowYamlAsync(string workflowName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workflowName))
            return Task.FromResult<string?>(null);

        return Task.FromResult(_lookup.GetYaml(workflowName.Trim()));
    }
}
