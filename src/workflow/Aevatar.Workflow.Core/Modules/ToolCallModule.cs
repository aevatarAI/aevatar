// ─────────────────────────────────────────────────────────────
// ToolCallModule — 工具调用模块
// 在工作流步骤中调用 Agent 的注册工具
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>工具调用模块。处理 type=tool_call 的步骤。</summary>
public sealed class ToolCallModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "tool_call";

    private readonly IEnumerable<IWorkflowToolSource> _toolSources;
    private readonly ILogger<ToolCallModule> _logger;
    private volatile Lazy<Task<IReadOnlyDictionary<string, IWorkflowTool>>>? _toolIndex;

    public ToolCallModule(
        IEnumerable<IWorkflowToolSource> toolSources,
        ILogger<ToolCallModule> logger)
    {
        _toolSources = toolSources ?? throw new ArgumentNullException(nameof(toolSources));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => "tool_call";
    public int Priority => 10;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true ||
        envelope.Payload?.Is(WorkflowResumedEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(WorkflowResumedEvent.Descriptor))
        {
            await HandleResumedAsync(payload.Unpack<WorkflowResumedEvent>(), ctx, ct);
            return;
        }

        var request = payload.Unpack<StepRequestEvent>();
        if (request.StepType != "tool_call") return;

        var toolName = request.Parameters.GetValueOrDefault("tool", "").Trim();
        var callId = ComposeWorkflowToolCallId(request);
        if (string.IsNullOrEmpty(toolName))
        {
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                ExecutionId = request.ExecutionId,
                Success = false, Error = "tool_call 缺少 tool 参数",
            }, TopologyAudience.Self, ct);
            return;
        }

        var argumentsJson = ResolveArgumentsJson(request);
        ctx.Logger.LogInformation("ToolCall: {StepId} → 工具 {Tool}", request.StepId, toolName);

        // 发布 Tool 调用开始事件（供观测/UI）
        await ctx.PublishAsync(new WorkflowToolCallStartedEvent
        {
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            CallId = callId,
            RunId = request.RunId,
            StepId = request.StepId,
        }, TopologyAudience.Self, ct);

        var toolIndex = await GetOrDiscoverAsync(ct);
        if (!toolIndex.TryGetValue(toolName, out var tool))
        {
            const string notFound = "tool not found or no tool sources configured";
            await PublishToolFailureAsync(ctx, request, toolName, notFound, ct);
            return;
        }

        try
        {
            var result = await ExecuteToolAsync(tool, argumentsJson, request, callId, ctx, ct);
            if (result.Outcome == WorkflowToolExecutionOutcome.ApprovalPending)
            {
                await SuspendForApprovalAsync(ctx, request, toolName, callId, argumentsJson, result, ct);
                return;
            }

            await PublishToolSuccessAsync(ctx, request, callId, result, ct);
        }
        catch (Exception ex)
        {
            await PublishToolFailureAsync(ctx, request, toolName, ex.Message, ct);
            ctx.Logger.LogWarning(ex, "ToolCall: step={StepId} tool={Tool} execution failed", request.StepId, toolName);
        }
    }

    private static Task<WorkflowToolExecutionResult> ExecuteToolAsync(
        IWorkflowTool tool,
        string argumentsJson,
        StepRequestEvent request,
        string callId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var callerCredential = WorkflowRunExecutionContextStateAccess.TryGetCallerCredential(ctx, out var credential)
            ? credential
            : new WorkflowCallerCredential();
        var runtimeContext = WorkflowRunExecutionContextStateAccess.GetWorkflowRuntimeContext(
            ctx,
            ctx.AgentId ?? string.Empty,
            request.RunId ?? string.Empty,
            request.StepId ?? string.Empty);
        return tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: argumentsJson,
                RunId: request.RunId ?? string.Empty,
                StepId: request.StepId ?? string.Empty,
                ExecutionId: request.ExecutionId ?? string.Empty,
                CallId: callId,
                ScopeId: ctx.ScopeId ?? string.Empty,
                CallerCredential: callerCredential,
                RuntimeContext: runtimeContext),
            ct);
    }

    private static Task<WorkflowToolExecutionResult> ExecuteToolWithGrantAsync(
        IWorkflowTool tool,
        PendingToolCallApprovalState pending,
        bool approved,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var callerCredential = WorkflowRunExecutionContextStateAccess.TryGetCallerCredential(ctx, out var credential)
            ? credential
            : new WorkflowCallerCredential();
        var runtimeContext = WorkflowRunExecutionContextStateAccess.GetWorkflowRuntimeContext(
            ctx,
            ctx.AgentId ?? string.Empty,
            pending.RunId ?? string.Empty,
            pending.StepId ?? string.Empty);
        return tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: pending.ArgumentsJson ?? string.Empty,
                RunId: pending.RunId ?? string.Empty,
                StepId: pending.StepId ?? string.Empty,
                ExecutionId: pending.ExecutionId ?? string.Empty,
                CallId: pending.ToolCallId ?? string.Empty,
                ScopeId: ctx.ScopeId ?? string.Empty,
                CallerCredential: callerCredential,
                RuntimeContext: runtimeContext,
                ApprovalGrant: new WorkflowToolApprovalGrant(
                    pending.ApprovalRequestId ?? string.Empty,
                    approved)),
            ct);
    }

    private async Task HandleResumedAsync(
        WorkflowResumedEvent resumed,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        if (!TryResolvePendingApproval(state, resumed, out var pendingKey, out var pending))
            return;

        state.PendingApprovals.Remove(pendingKey);
        await SaveStateAsync(state, ctx, ct);

        if (!resumed.Approved)
        {
            await PublishRejectedApprovalAsync(ctx, pending, ct);
            return;
        }

        var toolIndex = await GetOrDiscoverAsync(ct);
        if (!toolIndex.TryGetValue(pending.ToolName, out var tool))
        {
            const string notFound = "tool not found or no tool sources configured";
            await PublishToolFailureAsync(ctx, pending, notFound, ct);
            return;
        }

        try
        {
            var result = await ExecuteToolWithGrantAsync(tool, pending, approved: true, ctx, ct);
            if (result.Outcome == WorkflowToolExecutionOutcome.ApprovalPending)
            {
                await PublishToolFailureAsync(ctx, pending, "approved tool call returned approval pending again", ct);
                return;
            }

            await PublishToolSuccessAsync(ctx, pending, result, ct);
        }
        catch (Exception ex)
        {
            await PublishToolFailureAsync(ctx, pending, ex.Message, ct);
            ctx.Logger.LogWarning(
                ex,
                "ToolCall: run={RunId} step={StepId} tool={Tool} approved execution failed",
                pending.RunId,
                pending.StepId,
                pending.ToolName);
        }
    }

    private static async Task SuspendForApprovalAsync(
        IWorkflowExecutionContext ctx,
        StepRequestEvent request,
        string toolName,
        string callId,
        string argumentsJson,
        WorkflowToolExecutionResult result,
        CancellationToken ct)
    {
        var approval = result.ApprovalPending
                       ?? throw new InvalidOperationException("Tool approval pending outcome is missing approval details.");
        var pending = new PendingToolCallApprovalState
        {
            RunId = WorkflowRunIdNormalizer.Normalize(request.RunId),
            StepId = request.StepId ?? string.Empty,
            ExecutionId = request.ExecutionId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(approval.ToolName) ? toolName : approval.ToolName,
            ToolCallId = string.IsNullOrWhiteSpace(approval.ToolCallId) ? callId : approval.ToolCallId,
            ApprovalRequestId = approval.ApprovalRequestId ?? string.Empty,
            ArgumentsJson = string.IsNullOrWhiteSpace(approval.ArgumentsJson) ? argumentsJson : approval.ArgumentsJson,
            Input = request.Input ?? string.Empty,
        };
        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        state.PendingApprovals[BuildPendingApprovalKey(pending)] = pending;
        await SaveStateAsync(state, ctx, ct);

        await ctx.PublishAsync(new WorkflowSuspendedEvent
        {
            RunId = pending.RunId,
            StepId = pending.StepId,
            SuspensionType = "tool_approval",
            ToolApproval = new WorkflowToolApprovalSuspension
            {
                ExecutionId = pending.ExecutionId,
                ToolName = pending.ToolName,
                ToolCallId = pending.ToolCallId,
                ApprovalRequestId = pending.ApprovalRequestId,
                ArgumentsJson = pending.ArgumentsJson,
            },
        }, TopologyAudience.ParentAndChildren, ct);
    }

    private static bool TryResolvePendingApproval(
        ToolCallModuleState state,
        WorkflowResumedEvent resumed,
        out string pendingKey,
        out PendingToolCallApprovalState pending)
    {
        pendingKey = string.Empty;
        pending = new PendingToolCallApprovalState();

        if (string.IsNullOrWhiteSpace(resumed.RunId) ||
            string.IsNullOrWhiteSpace(resumed.StepId) ||
            string.IsNullOrWhiteSpace(resumed.ExecutionId) ||
            string.IsNullOrWhiteSpace(resumed.ApprovalRequestId))
        {
            return false;
        }

        pendingKey = BuildPendingApprovalKey(
            WorkflowRunIdNormalizer.Normalize(resumed.RunId),
            resumed.StepId,
            resumed.ExecutionId,
            resumed.ApprovalRequestId);
        if (!state.PendingApprovals.TryGetValue(pendingKey, out var resolvedPending))
            return false;

        pending = resolvedPending;
        return string.Equals(pending.RunId, WorkflowRunIdNormalizer.Normalize(resumed.RunId), StringComparison.Ordinal) &&
               string.Equals(pending.StepId, resumed.StepId, StringComparison.Ordinal) &&
               string.Equals(pending.ExecutionId, resumed.ExecutionId, StringComparison.Ordinal) &&
               string.Equals(pending.ApprovalRequestId, resumed.ApprovalRequestId, StringComparison.Ordinal);
    }

    private static string BuildPendingApprovalKey(PendingToolCallApprovalState pending) =>
        BuildPendingApprovalKey(
            pending.RunId,
            pending.StepId,
            pending.ExecutionId,
            pending.ApprovalRequestId);

    private static string BuildPendingApprovalKey(
        string runId,
        string stepId,
        string executionId,
        string approvalRequestId) =>
        $"{WorkflowRunIdNormalizer.Normalize(runId)}::{stepId}::{executionId}::{approvalRequestId}";

    private static Task SaveStateAsync(
        ToolCallModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.PendingApprovals.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

    private static async Task PublishToolSuccessAsync(
        IWorkflowExecutionContext ctx,
        StepRequestEvent request,
        string callId,
        WorkflowToolExecutionResult result,
        CancellationToken ct)
    {
        var completed = new WorkflowToolCallCompletedEvent
        {
            CallId = callId,
            Success = true,
            ResultJson = result.ResultJson,
            RunId = request.RunId,
            StepId = request.StepId,
        };
        if (result.ManagedHandoff != null)
            completed.ManagedHandoff = result.ManagedHandoff.Clone();

        await ctx.PublishAsync(completed, TopologyAudience.Self, ct);

        if (result.ManagedHandoff != null)
            return;

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            ExecutionId = request.ExecutionId,
            Success = true,
            Output = result.ResultJson,
        }, TopologyAudience.Self, ct);
    }

    private static async Task PublishToolSuccessAsync(
        IWorkflowExecutionContext ctx,
        PendingToolCallApprovalState pending,
        WorkflowToolExecutionResult result,
        CancellationToken ct)
    {
        var completed = new WorkflowToolCallCompletedEvent
        {
            CallId = pending.ToolCallId,
            Success = true,
            ResultJson = result.ResultJson,
            RunId = pending.RunId,
            StepId = pending.StepId,
        };
        if (result.ManagedHandoff != null)
            completed.ManagedHandoff = result.ManagedHandoff.Clone();

        await ctx.PublishAsync(completed, TopologyAudience.Self, ct);

        if (result.ManagedHandoff != null)
            return;

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = pending.RunId,
            ExecutionId = pending.ExecutionId,
            Success = true,
            Output = result.ResultJson,
        }, TopologyAudience.Self, ct);
    }

    private static Task PublishRejectedApprovalAsync(
        IWorkflowExecutionContext ctx,
        PendingToolCallApprovalState pending,
        CancellationToken ct) =>
        PublishToolFailureAsync(ctx, pending, "tool approval rejected", ct);

    private static string ResolveArgumentsJson(StepRequestEvent request)
    {
        var configuredArguments = request.Parameters.GetValueOrDefault("arguments", string.Empty);
        if (string.IsNullOrWhiteSpace(configuredArguments))
            configuredArguments = request.Parameters.GetValueOrDefault("args", string.Empty);

        if (!string.IsNullOrWhiteSpace(configuredArguments))
            return configuredArguments.Trim();

        return string.IsNullOrWhiteSpace(request.Input) ? "{}" : request.Input;
    }

    private static string ComposeWorkflowToolCallId(StepRequestEvent request)
    {
        var runId = Normalize(request.RunId);
        var stepId = Normalize(request.StepId);
        var executionId = Normalize(request.ExecutionId);

        if (runId != null && stepId != null && executionId != null)
            return $"workflow:{runId}:{stepId}:{executionId}";

        if (runId != null && stepId != null)
            return $"workflow:{runId}:{stepId}";

        return stepId ?? executionId ?? runId ?? string.Empty;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Task<IReadOnlyDictionary<string, IWorkflowTool>> GetOrDiscoverAsync(CancellationToken ct)
    {
        while (true)
        {
            var current = _toolIndex;
            if (TryGetReusableTask(current, out var cached))
                return cached;

            // Refactor (iter88/cluster-088):
            // Old: workflow tool discovery started before CompareExchange, so loser callers could
            // repeat source discovery and external MCP lifecycle work.
            // New: publish Lazy<Task<T>> before evaluation; only the winning Lazy starts discovery.
            var candidate = new Lazy<Task<IReadOnlyDictionary<string, IWorkflowTool>>>(
                () => DiscoverAllToolsAsync(_toolSources, _logger, ct),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var winner = Interlocked.CompareExchange(ref _toolIndex, candidate, current);
            if (ReferenceEquals(winner, current))
                return candidate.Value;
        }
    }

    private static bool TryGetReusableTask(
        Lazy<Task<IReadOnlyDictionary<string, IWorkflowTool>>>? current,
        out Task<IReadOnlyDictionary<string, IWorkflowTool>> task)
    {
        task = null!;
        if (current == null)
            return false;

        if (!current.IsValueCreated)
        {
            task = current.Value;
            return true;
        }

        var existing = current.Value;
        if (existing.IsFaulted || existing.IsCanceled)
            return false;

        task = existing;
        return true;
    }
    private static async Task<IReadOnlyDictionary<string, IWorkflowTool>> DiscoverAllToolsAsync(
        IEnumerable<IWorkflowToolSource> toolSources,
        ILogger logger,
        CancellationToken ct)
    {
        var index = new Dictionary<string, IWorkflowTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in toolSources)
        {
            IReadOnlyList<IWorkflowTool> tools;
            try
            {
                tools = await source.GetToolsAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Tool source discovery failed: {Source}", source.GetType().Name);
                continue;
            }

            foreach (var tool in tools)
                index[tool.Name] = tool;
        }

        return index;
    }

    private static async Task PublishToolFailureAsync(
        IWorkflowExecutionContext ctx,
        StepRequestEvent request,
        string toolName,
        string error,
        CancellationToken ct)
    {
        var errorMessage = $"tool '{toolName}' execution failed: {error}";

        await ctx.PublishAsync(new WorkflowToolCallCompletedEvent
        {
            CallId = ComposeWorkflowToolCallId(request),
            Success = false,
            Error = errorMessage,
            RunId = request.RunId,
            StepId = request.StepId,
        }, TopologyAudience.Self, ct);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            ExecutionId = request.ExecutionId,
            Success = false,
            Error = errorMessage,
        }, TopologyAudience.Self, ct);
    }

    private static async Task PublishToolFailureAsync(
        IWorkflowExecutionContext ctx,
        PendingToolCallApprovalState pending,
        string error,
        CancellationToken ct)
    {
        var errorMessage = $"tool '{pending.ToolName}' execution failed: {error}";

        await ctx.PublishAsync(new WorkflowToolCallCompletedEvent
        {
            CallId = pending.ToolCallId,
            Success = false,
            Error = errorMessage,
            RunId = pending.RunId,
            StepId = pending.StepId,
        }, TopologyAudience.Self, ct);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = pending.RunId,
            ExecutionId = pending.ExecutionId,
            Success = false,
            Error = errorMessage,
        }, TopologyAudience.Self, ct);
    }
}
