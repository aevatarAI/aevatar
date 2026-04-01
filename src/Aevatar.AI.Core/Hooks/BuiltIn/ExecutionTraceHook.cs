// ─────────────────────────────────────────────────────────────
// ExecutionTraceHook — 可观测性 Hook
// 在每个 hook 点位记录 trace 日志
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.Hooks;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Core.Hooks.BuiltIn;

/// <summary>在每个 hook 点位记录结构化 trace 日志。</summary>
public sealed class ExecutionTraceHook : IAIGAgentExecutionHook
{
    private readonly ILogger _logger;

    public ExecutionTraceHook(ILogger? logger = null) =>
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public string Name => "execution_trace";
    public int Priority => -1000; // 最先执行

    // ─── Foundation 级 hook（Event Handler） ───

    public Task OnEventHandlerStartAsync(GAgentExecutionHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] EventHandler Start: Agent={Agent}, Handler={Handler}", ctx.AgentId, ctx.HandlerName); return Task.CompletedTask; }

    public Task OnEventHandlerEndAsync(GAgentExecutionHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] EventHandler End: Agent={Agent}, Handler={Handler}, Duration={Dur}ms", ctx.AgentId, ctx.HandlerName, ctx.Duration?.TotalMilliseconds); return Task.CompletedTask; }

    public Task OnErrorAsync(GAgentExecutionHookContext ctx, Exception ex, CancellationToken ct)
    { _logger.LogError(ex, "[Trace] Error: Agent={Agent}, Handler={Handler}", ctx.AgentId, ctx.HandlerName); return Task.CompletedTask; }

    // ─── AI 级 hook（LLM / Tool） ───

    public Task OnLLMRequestStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] LLM Request Start: Agent={Agent}", ctx.AgentId); return Task.CompletedTask; }

    public Task OnLLMRequestEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] LLM Request End: Agent={Agent}", ctx.AgentId); return Task.CompletedTask; }

    public Task OnToolExecuteStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] Tool Start: {Tool}", ctx.ToolName); return Task.CompletedTask; }

    public Task OnToolExecuteEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] Tool End: {Tool}", ctx.ToolName); return Task.CompletedTask; }

    // ─── 上下文压缩 hook ───

    public Task OnCompactStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] Compact Start: Reason={Reason}", ctx.Items.TryGetValue("compression_reason", out var r) ? r : "unknown"); return Task.CompletedTask; }

    public Task OnCompactEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] Compact End: ToolResults={Compacted}, Truncated={Truncated}, Summarized={Summarized}", ctx.Items.TryGetValue("compacted_tool_results", out var c) ? c : 0, ctx.Items.TryGetValue("truncated_messages", out var t) ? t : 0, ctx.Items.TryGetValue("summarized", out var s) ? s : false); return Task.CompletedTask; }

    // ─── 工具审批 hook ───

    public Task OnToolApprovalRequestedAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] Approval Requested: Tool={Tool}, Mode={Mode}", ctx.ToolName, ctx.Items.TryGetValue("approval_mode", out var m) ? m : "unknown"); return Task.CompletedTask; }

    public Task OnToolApprovalCompletedAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] Approval Completed: Tool={Tool}, Decision={Decision}", ctx.ToolName, ctx.Items.TryGetValue("approval_decision", out var d) ? d : "unknown"); return Task.CompletedTask; }

    // ─── Post-Sampling hook ───

    public Task OnPostSamplingAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] PostSampling: Agent={Agent}, ToolCalls={ToolCalls}", ctx.AgentId, ctx.Items.TryGetValue("tool_call_count", out var c) ? c : 0); return Task.CompletedTask; }

    // ─── Tool 执行失败 hook ───

    public Task OnToolExecuteFailureAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    { _logger.LogWarning("[Trace] Tool Failure: Tool={Tool}, Error={Error}", ctx.ToolName, ctx.Items.TryGetValue("tool_error_message", out var e) ? e : "unknown"); return Task.CompletedTask; }

    // ─── 通知 hook ───

    public Task OnNotificationAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] Notification: Type={Type}", ctx.Items.TryGetValue("notification_type", out var t) ? t : "unknown"); return Task.CompletedTask; }

    // ─── Turn 完成/失败 hook ───

    public Task OnStopAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] Stop: Agent={Agent}, Rounds={Rounds}", ctx.AgentId, ctx.Items.TryGetValue("total_rounds", out var r) ? r : 0); return Task.CompletedTask; }

    public Task OnStopFailureAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    { _logger.LogError("[Trace] StopFailure: Agent={Agent}, Phase={Phase}, Error={Error}", ctx.AgentId, ctx.Items.TryGetValue("error_phase", out var p) ? p : "unknown", ctx.Items.TryGetValue("error_message", out var e) ? e : "unknown"); return Task.CompletedTask; }
}
