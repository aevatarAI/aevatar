// ─────────────────────────────────────────────────────────────
// ToolCallModule — 工具调用模块
// 在工作流步骤中调用 Agent 的注册工具
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>工具调用模块。处理 type=tool_call 的步骤。</summary>
public sealed class ToolCallModule : IEventModule<IWorkflowExecutionContext>
{
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
        await ctx.PublishAsync(new WorkflowToolCallStartedEvent
        {
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            CallId = request.StepId,
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
            var result = await tool.ExecuteAsync(
                new WorkflowToolExecutionRequest(argumentsJson, request.InputFileRefs),
                ct);

            await ctx.PublishAsync(new WorkflowToolCallCompletedEvent
            {
                CallId = request.StepId,
                Success = true,
                ResultJson = result,
                RunId = request.RunId,
                StepId = request.StepId,
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
            CallId = request.StepId,
            Success = false,
            Error = errorMessage,
            RunId = request.RunId,
            StepId = request.StepId,
        }, TopologyAudience.Self, ct);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = false,
            Error = errorMessage,
        }, TopologyAudience.Self, ct);
    }
}
