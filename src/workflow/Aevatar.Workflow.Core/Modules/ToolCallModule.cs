// ─────────────────────────────────────────────────────────────
// ToolCallModule — 工具调用模块
// 在工作流步骤中调用 Agent 的注册工具
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Abstractions.Credentials;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>工具调用模块。处理 type=tool_call 的步骤。</summary>
public sealed class ToolCallModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "tool_call";

    private readonly IEnumerable<IWorkflowToolSource> _toolSources;
    private readonly ILogger<ToolCallModule> _logger;
    private readonly IWorkflowCallerAccessTokenProvider? _callerAccessTokenProvider;
    private volatile Lazy<Task<IReadOnlyDictionary<string, IWorkflowTool>>>? _toolIndex;

    public ToolCallModule(
        IEnumerable<IWorkflowToolSource> toolSources,
        ILogger<ToolCallModule> logger,
        IWorkflowCallerAccessTokenProvider? callerAccessTokenProvider = null)
    {
        _toolSources = toolSources ?? throw new ArgumentNullException(nameof(toolSources));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _callerAccessTokenProvider = callerAccessTokenProvider;
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
            await HandleResumeAsync(payload.Unpack<WorkflowResumedEvent>(), ctx, ct);
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
            if (result.PendingApproval != null)
            {
                await SuspendForApprovalAsync(ctx, request, toolName, callId, result.PendingApproval, ct);
                return;
            }

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
        catch (Exception ex)
        {
            await PublishToolFailureAsync(ctx, request, toolName, ex.Message, ct);
            ctx.Logger.LogWarning(ex, "ToolCall: step={StepId} tool={Tool} execution failed", request.StepId, toolName);
        }
    }

    private async Task<WorkflowToolExecutionResult> ExecuteToolAsync(
        IWorkflowTool tool,
        string argumentsJson,
        StepRequestEvent request,
        string callId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct,
        ToolApprovalGrant? approvalGrant = null)
    {
        var credential = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(ctx, ct);
        var callerCredential = credential.Found
            ? await WorkflowCallerAccessTokenResolver.ResolveAsync(
                credential.Credential,
                _callerAccessTokenProvider,
                ct)
            : new WorkflowCallerCredential();
        var runtimeContext = WorkflowRunExecutionContextStateAccess.GetWorkflowRuntimeContext(
            ctx,
            ctx.AgentId ?? string.Empty,
            request.RunId ?? string.Empty,
            request.StepId ?? string.Empty);
        return await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: argumentsJson,
                RunId: request.RunId ?? string.Empty,
                StepId: request.StepId ?? string.Empty,
                ExecutionId: request.ExecutionId ?? string.Empty,
                CallId: callId,
                ScopeId: ctx.ScopeId ?? string.Empty,
                CallerCredential: callerCredential,
                RuntimeContext: runtimeContext,
                ApprovalGrant: approvalGrant,
                InputFileRefs: request.InputFileRefs,
                IdempotencyKey: request.IdempotencyKey ?? string.Empty,
                ScheduleId: ctx.ScheduleId ?? string.Empty),
            ct);
    }

    private async Task HandleResumeAsync(
        WorkflowResumedEvent resumed,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (resumed.ToolApproval == null)
            return;

        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        if (!TryResolvePending(state, resumed, out var pendingKey, out var pending))
            return;

        if (!resumed.Approved)
        {
            state.PendingApprovals.Remove(pendingKey);
            await SaveStateAsync(state, ctx, ct);
            await PublishToolFailureAsync(
                ctx,
                pending,
                BuildRejectedApprovalError(resumed),
                ct);
            return;
        }

        var toolIndex = await GetOrDiscoverAsync(ct);
        if (!toolIndex.TryGetValue(pending.ToolName, out var tool))
        {
            state.PendingApprovals.Remove(pendingKey);
            await SaveStateAsync(state, ctx, ct);
            await PublishToolFailureAsync(
                ctx,
                pending,
                "tool not found or no tool sources configured",
                ct);
            return;
        }

        try
        {
            state.PendingApprovals.Remove(pendingKey);
            await SaveStateAsync(state, ctx, ct);

            var result = await ExecuteToolAsync(
                tool,
                pending.ArgumentsJson,
                ToStepRequest(pending),
                pending.ToolCallId,
                ctx,
                ct,
                new ToolApprovalGrant(
                    pending.ApprovalRequestId,
                    pending.ToolName,
                    pending.ToolCallId));

            if (result.PendingApproval != null)
            {
                await SuspendForApprovalAsync(
                    ctx,
                    ToStepRequest(pending),
                    pending.ToolName,
                    pending.ToolCallId,
                    result.PendingApproval,
                    ct);
                return;
            }

            await PublishToolSuccessAsync(ctx, pending, result, ct);
        }
        catch (Exception ex)
        {
            await PublishToolFailureAsync(ctx, pending, ex.Message, ct);
            ctx.Logger.LogWarning(
                ex,
                "ToolCall: step={StepId} tool={Tool} approved replay failed",
                pending.StepId,
                pending.ToolName);
        }
    }

    private static bool TryResolvePending(
        ToolCallModuleState state,
        WorkflowResumedEvent resumed,
        out string pendingKey,
        out PendingToolCallApprovalState pending)
    {
        pendingKey = BuildPendingKey(
            resumed.RunId,
            resumed.StepId,
            resumed.ToolApproval?.ExecutionId,
            resumed.ToolApproval?.ToolCallId,
            resumed.ToolApproval?.ApprovalRequestId);
        pending = new PendingToolCallApprovalState();
        if (string.IsNullOrWhiteSpace(resumed.RunId) ||
            string.IsNullOrWhiteSpace(resumed.StepId) ||
            resumed.ToolApproval == null ||
            string.IsNullOrWhiteSpace(resumed.ToolApproval.ExecutionId) ||
            string.IsNullOrWhiteSpace(resumed.ToolApproval.ToolCallId) ||
            string.IsNullOrWhiteSpace(resumed.ToolApproval.ApprovalRequestId))
        {
            return false;
        }

        if (!state.PendingApprovals.TryGetValue(pendingKey, out var resolvedPending))
            return false;

        pending = resolvedPending;
        return string.Equals(pending.RunId, NormalizeRequired(resumed.RunId), StringComparison.Ordinal) &&
               string.Equals(pending.StepId, NormalizeRequired(resumed.StepId), StringComparison.Ordinal) &&
               string.Equals(pending.ExecutionId, NormalizeRequired(resumed.ToolApproval.ExecutionId), StringComparison.Ordinal) &&
               string.Equals(pending.ToolCallId, NormalizeRequired(resumed.ToolApproval.ToolCallId), StringComparison.Ordinal) &&
               string.Equals(pending.ApprovalRequestId, NormalizeRequired(resumed.ToolApproval.ApprovalRequestId), StringComparison.Ordinal);
    }

    private static async Task SuspendForApprovalAsync(
        IWorkflowExecutionContext ctx,
        StepRequestEvent request,
        string toolName,
        string callId,
        WorkflowToolApprovalPendingOutcome pending,
        CancellationToken ct)
    {
        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        var pendingState = new PendingToolCallApprovalState
        {
            RunId = NormalizeRequired(request.RunId),
            StepId = NormalizeRequired(request.StepId),
            ExecutionId = NormalizeRequired(request.ExecutionId),
            ToolName = NormalizeRequired(toolName),
            ToolCallId = NormalizeRequired(callId),
            ApprovalRequestId = NormalizeRequired(pending.ApprovalRequestId),
            ArgumentsJson = pending.ArgumentsJson ?? string.Empty,
            IdempotencyKey = request.IdempotencyKey ?? string.Empty,
        };
        pendingState.InputFileRefs.Add(request.InputFileRefs.Select(static fileRef => fileRef.Clone()));
        state.PendingApprovals[BuildPendingKey(pendingState)] = pendingState;
        await SaveStateAsync(state, ctx, ct);

        await ctx.PublishAsync(new WorkflowSuspendedEvent
        {
            RunId = pendingState.RunId,
            StepId = pendingState.StepId,
            SuspensionType = "tool_approval",
            Prompt = $"Approve tool '{pendingState.ToolName}' execution?",
            ToolApproval = new WorkflowToolApprovalSuspension
            {
                ExecutionId = pendingState.ExecutionId,
                ToolName = pendingState.ToolName,
                ToolCallId = pendingState.ToolCallId,
                ApprovalRequestId = pendingState.ApprovalRequestId,
            },
        }, TopologyAudience.ParentAndChildren, ct);
    }

    private static Task PublishToolSuccessAsync(
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

        return PublishToolSuccessAsync(ctx, pending, completed, result, ct);
    }

    private static async Task PublishToolSuccessAsync(
        IWorkflowExecutionContext ctx,
        PendingToolCallApprovalState pending,
        WorkflowToolCallCompletedEvent completed,
        WorkflowToolExecutionResult result,
        CancellationToken ct)
    {
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

    private static Task PublishToolFailureAsync(
        IWorkflowExecutionContext ctx,
        PendingToolCallApprovalState pending,
        string error,
        CancellationToken ct) =>
        PublishToolFailureAsync(
            ctx,
            ToStepRequest(pending),
            pending.ToolName,
            error,
            ct);

    private static StepRequestEvent ToStepRequest(PendingToolCallApprovalState pending) =>
        new()
        {
            StepId = pending.StepId,
            StepType = "tool_call",
            RunId = pending.RunId,
            ExecutionId = pending.ExecutionId,
            Input = pending.ArgumentsJson,
            IdempotencyKey = pending.IdempotencyKey,
            Parameters = { ["tool"] = pending.ToolName },
            InputFileRefs = { pending.InputFileRefs.Select(static fileRef => fileRef.Clone()) },
        };

    private static string BuildRejectedApprovalError(WorkflowResumedEvent resumed)
    {
        var feedback = Normalize(resumed.Feedback)
                       ?? Normalize(resumed.UserInput)
                       ?? Normalize(resumed.EditedContent);
        return feedback == null
            ? "approval rejected"
            : $"approval rejected: {feedback}";
    }

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

    private static string NormalizeRequired(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string BuildPendingKey(PendingToolCallApprovalState pending) =>
        BuildPendingKey(
            pending.RunId,
            pending.StepId,
            pending.ExecutionId,
            pending.ToolCallId,
            pending.ApprovalRequestId);

    private static string BuildPendingKey(
        string? runId,
        string? stepId,
        string? executionId,
        string? toolCallId,
        string? approvalRequestId) =>
        $"{NormalizeRequired(runId)}:{NormalizeRequired(stepId)}:{NormalizeRequired(executionId)}:{NormalizeRequired(toolCallId)}:{NormalizeRequired(approvalRequestId)}";

    private static Task SaveStateAsync(
        ToolCallModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.PendingApprovals.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

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
}
