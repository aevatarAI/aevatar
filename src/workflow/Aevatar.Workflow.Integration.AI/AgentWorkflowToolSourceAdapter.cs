using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Auditing;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Integration.AI;

public sealed class AgentWorkflowToolSourceAdapter(
    IEnumerable<IAgentToolSource> agentToolSources,
    IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
    IToolApprovalHandler? approvalHandler = null,
    IEnumerable<IAIGAgentExecutionHook>? hooks = null,
    ILogger<AgentWorkflowToolSourceAdapter>? logger = null) : IWorkflowToolSource
{
    private readonly IEnumerable<IAgentToolSource> _agentToolSources =
        agentToolSources ?? throw new ArgumentNullException(nameof(agentToolSources));
    private readonly IReadOnlyList<IToolCallMiddleware> _toolMiddlewares =
        ToolCallMiddlewareChainFactory.ForAgentRuntime(
            toolMiddlewares ?? [],
            approvalHandler,
            hooks == null ? null : new AgentHookPipeline(hooks));
    private readonly ILogger<AgentWorkflowToolSourceAdapter> _logger =
        logger ?? NullLogger<AgentWorkflowToolSourceAdapter>.Instance;

    public async Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
    {
        var workflowTools = new List<IWorkflowTool>();
        foreach (var source in _agentToolSources)
        {
            var tools = await source.DiscoverToolsAsync(ct).ConfigureAwait(false);
            foreach (var tool in tools)
                workflowTools.Add(new AgentWorkflowToolAdapter(tool, _toolMiddlewares, _logger));
        }

        return workflowTools;
    }

    private sealed class AgentWorkflowToolAdapter(
        IAgentTool tool,
        IReadOnlyList<IToolCallMiddleware> toolMiddlewares,
        ILogger logger) : IWorkflowTool
    {
        private readonly IAgentTool _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        private readonly IReadOnlyList<IToolCallMiddleware> _toolMiddlewares =
            toolMiddlewares ?? throw new ArgumentNullException(nameof(toolMiddlewares));
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
            using var scope = AgentToolContextScope.Push(toolContext);
            var toolCallContext = new ToolCallContext
            {
                Tool = _tool,
                ToolName = _tool.Name,
                ToolCallId = Normalize(request.CallId) ?? string.Empty,
                ArgumentsJson = request.ArgumentsJson,
                CancellationToken = ct,
                ExecutionContext = toolContext,
                ApprovalGrant = request.ApprovalGrant == null
                    ? null
                    : new Aevatar.AI.Abstractions.Middleware.ToolApprovalGrant(
                        request.ApprovalGrant.ApprovalRequestId,
                        request.ApprovalGrant.ToolName,
                        request.ApprovalGrant.ToolCallId),
            };

            await MiddlewarePipeline.RunToolCallAsync(_toolMiddlewares, toolCallContext, async () =>
            {
                if (toolCallContext.Terminate)
                    return;

                toolCallContext.Result = await _tool.ExecuteAsync(toolCallContext.ArgumentsJson, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            if (toolCallContext.Terminate &&
                toolCallContext.TerminationKind == ToolCallTerminationKind.ApprovalPending &&
                toolCallContext.PendingApproval != null)
            {
                return new WorkflowToolExecutionResult(
                    string.Empty,
                    PendingApproval: ToWorkflowToolApprovalPendingOutcome(toolCallContext.PendingApproval));
            }

            if (toolCallContext.Terminate)
                throw new InvalidOperationException(FormatMiddlewareTermination(toolCallContext));

            var resultJson = toolCallContext.Result
                             ?? throw new InvalidOperationException(
                                 $"Tool '{_tool.Name}' returned no result.");
            var receipt = ToolCallReceiptFinalizer.Finalize(toolCallContext).Receipt;
            return new WorkflowToolExecutionResult(
                resultJson,
                ToWorkflowManagedHandoffOutcome(receipt?.ManagedWorkflowHandoff));
        }

        private static WorkflowToolApprovalPendingOutcome ToWorkflowToolApprovalPendingOutcome(
            ToolApprovalPendingContext pending) =>
            new(
                pending.ApprovalRequestId,
                pending.ToolName,
                pending.ToolCallId,
                pending.ArgumentsJson,
                pending.ApprovalMode.ToString(),
                pending.IsReadOnly,
                pending.IsDestructive);

        private static string? Normalize(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string FormatMiddlewareTermination(ToolCallContext context)
        {
            var reason = string.IsNullOrWhiteSpace(context.TerminationReason)
                ? context.Result
                : context.TerminationReason;
            var suffix = string.IsNullOrWhiteSpace(reason)
                ? string.Empty
                : $": {reason}";
            return $"Tool '{context.ToolName}' execution terminated by middleware ({context.TerminationKind}){suffix}";
        }

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
