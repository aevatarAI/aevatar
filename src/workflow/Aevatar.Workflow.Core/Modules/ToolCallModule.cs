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
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>工具调用模块。处理 type=tool_call 的步骤。</summary>
public sealed class ToolCallModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "tool_call";

    private readonly IEnumerable<IAgentToolSource> _toolSources;
    private readonly IReadOnlyList<IToolCallMiddleware> _middlewares;
    private readonly ILogger<ToolCallModule> _logger;
    private volatile Lazy<Task<IReadOnlyDictionary<string, IAgentTool>>>? _toolIndex;

    public ToolCallModule(
        IEnumerable<IAgentToolSource> toolSources,
        IEnumerable<IToolCallMiddleware> middlewares,
        ILogger<ToolCallModule> logger)
    {
        _toolSources = toolSources ?? throw new ArgumentNullException(nameof(toolSources));
        _middlewares = (middlewares ?? throw new ArgumentNullException(nameof(middlewares))).ToList();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        if (string.IsNullOrEmpty(toolName))
        {
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
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
            CallId = request.StepId,
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
            var toolContext = new ToolCallContext
            {
                Tool = tool,
                ToolName = toolName,
                ToolCallId = request.StepId,
                ArgumentsJson = argumentsJson,
                CancellationToken = ct,
            };
            await ExecuteWithMiddlewareAsync(toolContext);

            if (toolContext.PendingApproval != null)
            {
                await PublishPendingApprovalAsync(ctx, request, toolContext.PendingApproval, argumentsJson, ct);
                return;
            }

            if (toolContext.Terminate)
            {
                await PublishToolFailureAsync(ctx, request, toolName, toolContext.Result ?? "tool execution terminated", ct);
                return;
            }

            var result = toolContext.Result ?? string.Empty;

            await ctx.PublishAsync(new ToolResultEvent
            {
                CallId = request.StepId,
                Success = true,
                ResultJson = result,
            }, TopologyAudience.Self, ct);

            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = true,
                Output = result,
            }, TopologyAudience.Self, ct);
        }
        catch (Exception ex)
        {
            await PublishToolFailureAsync(ctx, request, toolName, ex.Message, ct);
            ctx.Logger.LogWarning(ex, "ToolCall: step={StepId} tool={Tool} execution failed", request.StepId, toolName);
        }
    }

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
            CallId = request.StepId,
            Success = false,
            Error = errorMessage,
        }, TopologyAudience.Self, ct);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = false,
            Error = errorMessage,
        }, TopologyAudience.Self, ct);
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
}
