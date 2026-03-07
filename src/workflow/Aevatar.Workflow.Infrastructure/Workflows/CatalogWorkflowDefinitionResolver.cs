using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Infrastructure.Workflows;

public sealed class CatalogWorkflowDefinitionResolver : IWorkflowDefinitionResolver
{
    private readonly IServiceProvider _services;

    public CatalogWorkflowDefinitionResolver(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public async Task<string?> GetWorkflowYamlAsync(string workflowName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workflowName))
            return null;

        var lookup = _services.GetService<IWorkflowDefinitionLookupService>()
                     ?? _services.GetService<IWorkflowDefinitionCatalog>();
        return lookup == null
            ? null
            : await lookup.GetYamlAsync(workflowName.Trim(), ct);
    }
}
