using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Integration.AI;

public sealed class AgentWorkflowToolSourceAdapter(
    IEnumerable<IAgentToolSource> agentToolSources,
    IAgentToolExecutionPort toolExecutionPort,
    ILogger<AgentWorkflowToolSourceAdapter>? logger = null) : IWorkflowToolSource
{
    private readonly IEnumerable<IAgentToolSource> _agentToolSources =
        agentToolSources ?? throw new ArgumentNullException(nameof(agentToolSources));
    private readonly IAgentToolExecutionPort _toolExecutionPort =
        toolExecutionPort ?? throw new ArgumentNullException(nameof(toolExecutionPort));
    private readonly ILogger<AgentWorkflowToolSourceAdapter> _logger =
        logger ?? NullLogger<AgentWorkflowToolSourceAdapter>.Instance;

    public async Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
    {
        var workflowTools = new List<IWorkflowTool>();
        foreach (var source in _agentToolSources)
        {
            var tools = await source.DiscoverToolsAsync(ct).ConfigureAwait(false);
            foreach (var tool in tools)
                workflowTools.Add(new AgentWorkflowToolAdapter(tool, _toolExecutionPort, _logger));
        }

        return workflowTools;
    }

    private sealed class AgentWorkflowToolAdapter(
        IAgentTool tool,
        IAgentToolExecutionPort toolExecutionPort,
        ILogger logger) : IWorkflowTool
    {
        private readonly IAgentTool _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        private readonly IAgentToolExecutionPort _toolExecutionPort =
            toolExecutionPort ?? throw new ArgumentNullException(nameof(toolExecutionPort));
        private readonly ILogger _logger = logger ?? NullLogger.Instance;

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
                    RequestId = Normalize(request.RunId),
                    CallId = Normalize(request.CallId),
                    IdempotencyKey = Normalize(request.IdempotencyKey),
                },
                Caller = credentialContext.Caller with
                {
                    ScopeId = Normalize(request.ScopeId),
                },
                Schedule = new AgentToolScheduleContext(Normalize(request.ScheduleId)),
            };
            _logger.LogInformation(
                "Workflow tool credential context prepared. toolName={ToolName} scopeId={ScopeId} rootRunId={RootRunId} parentRunId={ParentRunId} parentStepId={ParentStepId} hasCallerCredentialBearer={HasCallerCredentialBearer} hasNyxIdAccessToken={HasNyxIdAccessToken} hasNyxIdOrgToken={HasNyxIdOrgToken}",
                _tool.Name,
                request.ScopeId ?? string.Empty,
                request.RuntimeContext.RootRunId ?? string.Empty,
                request.RuntimeContext.ParentRunId ?? string.Empty,
                request.RuntimeContext.ParentStepId ?? string.Empty,
                !string.IsNullOrWhiteSpace(request.CallerCredential?.BearerToken),
                !string.IsNullOrWhiteSpace(toolContext.Credentials.NyxIdAccessToken),
                !string.IsNullOrWhiteSpace(toolContext.Credentials.NyxIdOrgToken));
            var outcome = await _toolExecutionPort.ExecuteAsync(
                new AgentToolExecutionRequest(
                    _tool,
                    request.ArgumentsJson,
                    toolContext,
                    AgentToolApprovalContinuationMode.ActorOwned,
                    request.ApprovalGrant == null
                        ? null
                        : new AgentToolApprovalGrant(
                            request.ApprovalGrant.ApprovalRequestId,
                            request.RunId,
                            request.ApprovalGrant.ToolName,
                            request.ApprovalGrant.ToolCallId,
                            AgentToolArgumentsDigest.ComputeSha256(request.ArgumentsJson))),
                ct).ConfigureAwait(false);

            if (outcome.Kind == AgentToolExecutionOutcomeKind.ApprovalRequired)
            {
                return new WorkflowToolExecutionResult(
                    string.Empty,
                    PendingApproval: new WorkflowToolApprovalPendingOutcome(
                        outcome.Receipt.ApprovalRequestId,
                        outcome.Receipt.ToolName,
                        outcome.Receipt.CallId,
                        request.ArgumentsJson,
                        outcome.Receipt.ApprovalMode.ToString(),
                        IsReadOnly: !outcome.IsMutation,
                        outcome.Receipt.IsDestructive));
            }

            if (outcome.Kind is not (AgentToolExecutionOutcomeKind.Executed or
                AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(outcome.SafeMessage)
                        ? outcome.FailureCode
                        : outcome.SafeMessage);
            }

            return new WorkflowToolExecutionResult(
                outcome.ResultJson,
                ToWorkflowManagedHandoffOutcome(outcome.Receipt.ManagedWorkflowHandoff));
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
