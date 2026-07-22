using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Workflow.Tools;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.AI.ToolProviders.Workflow;

/// <summary>Provides selectively composable tools for the global runnable workflow catalog.</summary>
public sealed class WorkflowCatalogAgentToolSource : IAgentToolSource
{
    private readonly IWorkflowCatalogPort? _catalog;

    public WorkflowCatalogAgentToolSource(IWorkflowCatalogPort? catalog = null)
    {
        _catalog = catalog;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IAgentTool>>(
            _catalog is null
                ? []
                : [new ListAevatarWorkflowsTool(_catalog), new GetAevatarWorkflowTool(_catalog)]);
    }
}
