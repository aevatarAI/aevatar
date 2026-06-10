using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
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

        public async Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
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
            var resultJson = await _tool.ExecuteAsync(request.ArgumentsJson, ct).ConfigureAwait(false);
            var receipt = _tool.CreateSuccessReceipt(request.CallId, _tool.Name, resultJson);
            return new WorkflowToolExecutionResult(
                resultJson,
                ToWorkflowManagedHandoffOutcome(receipt?.ManagedWorkflowHandoff));
        }

        private static string? Normalize(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static WorkflowManagedHandoffOutcome? ToWorkflowManagedHandoffOutcome(
            ManagedWorkflowHandoffReceipt? receipt)
        {
            if (receipt == null || string.IsNullOrWhiteSpace(receipt.InvocationId))
                return null;

            return new WorkflowManagedHandoffOutcome
            {
                ParentActorId = receipt.ParentActorId ?? string.Empty,
                ParentRunId = receipt.ParentRunId ?? string.Empty,
                ParentStepId = receipt.ParentStepId ?? string.Empty,
                InvocationId = receipt.InvocationId ?? string.Empty,
                ChildRunId = receipt.ChildRunId ?? string.Empty,
                StreamTopic = receipt.StreamTopic ?? string.Empty,
            };
        }
    }
}
