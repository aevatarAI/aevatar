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

        public async Task<string> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var workflowRuntimeContext = new AgentWorkflowRuntimeContext(
                Normalize(request.RuntimeContext.ParentActorId),
                Normalize(request.RuntimeContext.ParentRunId),
                Normalize(request.RuntimeContext.ParentStepId),
                Normalize(request.RuntimeContext.RootRunId),
                Math.Max(0, request.RuntimeContext.Depth));
            var credentialContext = WorkflowCallerCredentialToolContextMapper.FromCredential(
                request.CallerCredential,
                workflowRuntimeContext);
            var toolContext = credentialContext with
            {
                Request = credentialContext.Request with
                {
                    CallId = Normalize(request.CallId),
                },
                Caller = credentialContext.Caller with
                {
                    ScopeId = Normalize(request.ScopeId),
                },
            };
            using var scope = AgentToolContextScope.Push(toolContext);
            return await _tool.ExecuteAsync(request.ArgumentsJson, ct).ConfigureAwait(false);
        }

        private static string? Normalize(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
