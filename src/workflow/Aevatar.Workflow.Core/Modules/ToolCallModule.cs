// ─────────────────────────────────────────────────────────────
// ToolCallModule — 工具调用模块
// 在工作流步骤中调用 Agent 的注册工具
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>工具调用模块。处理 type=tool_call 的步骤。</summary>
public sealed class ToolCallModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "tool_call";

    private readonly IEnumerable<IAgentToolSource> _toolSources;
    private readonly ILogger<ToolCallModule> _logger;
    private readonly IAgentToolExecutionPort? _executionPort;
    private volatile Lazy<Task<IReadOnlyDictionary<string, IAgentTool>>>? _toolIndex;

    [ActivatorUtilitiesConstructor]
    public ToolCallModule(
        IEnumerable<IAgentToolSource> toolSources,
        ILogger<ToolCallModule> logger,
        IAgentToolExecutionPort? executionPort = null)
    {
        _toolSources = toolSources ?? throw new ArgumentNullException(nameof(toolSources));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _executionPort = executionPort;
    }

    public ToolCallModule(
        IEnumerable<IAgentToolSource> toolSources,
        IEnumerable<IToolCallMiddleware> middlewares,
        ILogger<ToolCallModule> logger)
        : this(
            toolSources,
            logger,
            new LocalMiddlewareExecutionPort(middlewares))
    {
    }

    public string Name => "tool_call";
    public int Priority => 10;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

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

        var argumentsJson = string.IsNullOrWhiteSpace(request.Input) ? "{}" : request.Input;
        ctx.Logger.LogInformation("ToolCall: {StepId} → 工具 {Tool}", request.StepId, toolName);

        // 发布 Tool 调用开始事件（供观测/UI）
        await ctx.PublishAsync(new ToolCallEvent
        {
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            CallId = callId,
        }, TopologyAudience.Self, ct);

        var toolIndex = await GetOrDiscoverAsync(ct);
        if (!toolIndex.TryGetValue(toolName, out var tool))
        {
            const string notFound = "tool not found or no tool sources configured";
            await PublishToolFailureAsync(ctx, request, toolName, notFound, ct);
            return;
        }

        if (_executionPort == null)
        {
            await PublishToolFailureAsync(
                ctx,
                request,
                toolName,
                "agent tool execution port is not configured",
                ct);
            return;
        }

        try
        {
            var toolContext = BuildToolContext(ctx, callId);
            var result = await _executionPort.ExecuteAsync(
                new AgentToolExecutionRequest(
                    Tool: tool,
                    ToolName: toolName,
                    ToolCallId: callId,
                    ArgumentsJson: argumentsJson,
                    ExecutionContext: toolContext),
                ct);

            if (result.Status == AgentToolExecutionStatus.ApprovalPending)
            {
                await PublishPendingApprovalAsync(
                    ctx,
                    request,
                    BuildPendingApprovalContext(toolName, callId, argumentsJson, result),
                    argumentsJson,
                    ct);
                return;
            }

            if (result.Status != AgentToolExecutionStatus.Succeeded)
            {
                await PublishToolFailureAsync(
                    ctx,
                    request,
                    toolName,
                    BuildExecutionFailure(result),
                    ct);
                return;
            }

            var resultJson = result.ResultJson ?? string.Empty;

            await ctx.PublishAsync(new ToolResultEvent
            {
                CallId = callId,
                Success = true,
                ResultJson = resultJson,
            }, TopologyAudience.Self, ct);

            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                ExecutionId = request.ExecutionId,
                Success = true,
                Output = resultJson,
            }, TopologyAudience.Self, ct);
        }
        catch (Exception ex)
        {
            await PublishToolFailureAsync(ctx, request, toolName, ex.Message, ct);
            ctx.Logger.LogWarning(ex, "ToolCall: step={StepId} tool={Tool} execution failed", request.StepId, toolName);
        }
    }

    private static AgentToolExecutionContext BuildToolContext(
        IWorkflowExecutionContext ctx,
        string callId)
    {
        var existing = WorkflowToolExecutionRuntimeContextAccess.GetToolContext(ctx)
                       ?? AgentToolExecutionContext.Empty;

        return existing with
        {
            Request = existing.Request with
            {
                CallId = callId
            }
        };
    }

    private static string BuildExecutionFailure(AgentToolExecutionResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            return result.ErrorMessage;

        return result.Status switch
        {
            AgentToolExecutionStatus.ApprovalDenied => "tool execution denied by approval policy",
            AgentToolExecutionStatus.ApprovalTimedOut => "tool approval timed out",
            AgentToolExecutionStatus.ApprovalPending => "tool approval is pending",
            AgentToolExecutionStatus.MiddlewareTerminated => "tool execution terminated by middleware",
            AgentToolExecutionStatus.Failed => "tool execution failed",
            _ => $"tool execution did not succeed: {result.Status}",
        };
    }

    private static ToolApprovalPendingContext BuildPendingApprovalContext(
        string toolName,
        string toolCallId,
        string argumentsJson,
        AgentToolExecutionResult result) =>
        new(
            TryReadApprovalRequestId(result.ResultJson) ?? Guid.NewGuid().ToString("N"),
            toolName,
            toolCallId,
            argumentsJson,
            ToolApprovalMode.AlwaysRequire,
            IsReadOnly: false,
            IsDestructive: true);

    private static string? TryReadApprovalRequestId(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return null;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(resultJson);
            return document.RootElement.TryGetProperty("request_id", out var requestIdElement)
                ? Normalize(requestIdElement.GetString())
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
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

    private Task<IReadOnlyDictionary<string, IAgentTool>> GetOrDiscoverAsync(CancellationToken ct)
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
            var candidate = new Lazy<Task<IReadOnlyDictionary<string, IAgentTool>>>(
                () => DiscoverAllToolsAsync(_toolSources, _logger, ct),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var winner = Interlocked.CompareExchange(ref _toolIndex, candidate, current);
            if (ReferenceEquals(winner, current))
                return candidate.Value;
        }
    }

    private static bool TryGetReusableTask(
        Lazy<Task<IReadOnlyDictionary<string, IAgentTool>>>? current,
        out Task<IReadOnlyDictionary<string, IAgentTool>> task)
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
    private static async Task<IReadOnlyDictionary<string, IAgentTool>> DiscoverAllToolsAsync(
        IEnumerable<IAgentToolSource> toolSources,
        ILogger logger,
        CancellationToken ct)
    {
        var index = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in toolSources)
        {
            IReadOnlyList<IAgentTool> tools;
            try
            {
                tools = await source.DiscoverToolsAsync(ct);
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

        await ctx.PublishAsync(new ToolResultEvent
        {
            CallId = ComposeWorkflowToolCallId(request),
            Success = false,
            Error = errorMessage,
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

    private static async Task PublishPendingApprovalAsync(
        IWorkflowExecutionContext ctx,
        StepRequestEvent request,
        ToolApprovalPendingContext pending,
        string input,
        CancellationToken ct)
    {
        var runId = request.RunId ?? string.Empty;
        var stepId = request.StepId ?? string.Empty;
        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        state.PendingApprovals[BuildPendingApprovalKey(runId, stepId, pending)] = new PendingToolCallApprovalState
        {
            RunId = runId,
            StepId = stepId,
            ExecutionId = request.ExecutionId ?? string.Empty,
            ToolName = pending.ToolName,
            ToolCallId = pending.ToolCallId,
            ApprovalRequestId = pending.ApprovalRequestId,
            ArgumentsJson = pending.ArgumentsJson,
            Input = input,
        };
        await WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);

        await ctx.PublishAsync(new WorkflowSuspendedEvent
        {
            RunId = runId,
            StepId = stepId,
            SuspensionType = "tool_approval",
            ToolApproval = new WorkflowToolApprovalSuspension
            {
                ExecutionId = request.ExecutionId ?? string.Empty,
                ToolName = pending.ToolName,
                ToolCallId = pending.ToolCallId,
                ApprovalRequestId = pending.ApprovalRequestId,
                ArgumentsJson = pending.ArgumentsJson,
            },
        }, TopologyAudience.ParentAndChildren, ct);
    }

    private static string BuildPendingApprovalKey(
        string runId,
        string stepId,
        ToolApprovalPendingContext pending) =>
        $"{runId}:{stepId}:{pending.ToolCallId}:{pending.ApprovalRequestId}";

    private sealed class LocalMiddlewareExecutionPort : IAgentToolExecutionPort
    {
        private readonly IReadOnlyList<IToolCallMiddleware> _middlewares;

        public LocalMiddlewareExecutionPort(IEnumerable<IToolCallMiddleware> middlewares)
        {
            _middlewares = (middlewares ?? throw new ArgumentNullException(nameof(middlewares))).ToList();
        }

        public async Task<AgentToolExecutionResult> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            var context = new ToolCallContext
            {
                Tool = request.Tool,
                ToolName = request.ToolName,
                ToolCallId = request.ToolCallId,
                ArgumentsJson = request.ArgumentsJson,
                CancellationToken = ct,
            };

            using (AgentToolContextScope.Push(request.ExecutionContext))
            {
                await ExecuteWithMiddlewareAsync(context);
            }

            if (context.Terminate)
            {
                return new AgentToolExecutionResult(
                    MapTerminationStatus(context.TerminationKind),
                    context.Result,
                    context.TerminationReason);
            }

            return context.Result == null
                ? AgentToolExecutionResult.Failed($"Tool '{context.ToolName}' returned no result.")
                : AgentToolExecutionResult.Succeeded(context.Result);
        }

        private Task ExecuteWithMiddlewareAsync(ToolCallContext context)
        {
            var index = -1;

            Task InvokeNextAsync()
            {
                index++;
                if (index < _middlewares.Count)
                    return _middlewares[index].InvokeAsync(context, InvokeNextAsync);

                return ExecuteToolAsync(context);
            }

            return InvokeNextAsync();
        }

        private static async Task ExecuteToolAsync(ToolCallContext context)
        {
            if (context.Terminate)
                return;

            context.Result = await context.Tool.ExecuteAsync(context.ArgumentsJson, context.CancellationToken);
        }

        private static AgentToolExecutionStatus MapTerminationStatus(ToolCallTerminationKind kind) =>
            kind switch
            {
                ToolCallTerminationKind.ApprovalDenied => AgentToolExecutionStatus.ApprovalDenied,
                ToolCallTerminationKind.ApprovalTimedOut => AgentToolExecutionStatus.ApprovalTimedOut,
                ToolCallTerminationKind.ApprovalPending => AgentToolExecutionStatus.ApprovalPending,
                _ => AgentToolExecutionStatus.MiddlewareTerminated,
            };
    }
}
