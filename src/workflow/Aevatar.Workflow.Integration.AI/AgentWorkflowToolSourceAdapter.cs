using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Core.Modules;

namespace Aevatar.Workflow.Integration.AI;

public sealed class AgentWorkflowToolSourceAdapter(IEnumerable<IAgentToolSource> agentToolSources) : IWorkflowToolSource
{
    private readonly IEnumerable<IAgentToolSource> _agentToolSources =
        agentToolSources ?? throw new ArgumentNullException(nameof(agentToolSources));

    public async Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
    {
        var workflowTools = new List<IWorkflowTool>();
        foreach (var source in _agentToolSources)
        {
            var tools = await source.DiscoverToolsAsync(ct).ConfigureAwait(false);
            foreach (var tool in tools)
                workflowTools.Add(new AgentWorkflowToolAdapter(tool));
        }

        return workflowTools;
    }

    private sealed class AgentWorkflowToolAdapter(IAgentTool tool) : IWorkflowTool
    {
        private readonly IAgentTool _tool = tool ?? throw new ArgumentNullException(nameof(tool));

        public string Name => _tool.Name;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            _tool.ExecuteAsync(argumentsJson, ct);
    }
}
