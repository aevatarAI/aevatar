// ─────────────────────────────────────────────────────────────
// ExecutionTraceHook — 可观测性 Hook
// 在每个 hook 点位记录 trace 日志
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.LLMProviders;
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
    {
        var response = ctx.LLMResponse as LLMResponse;
        var content = response?.Content;
        var toolCalls = response?.ToolCalls;
        var contentPreview = content != null
            ? (content.Length > 300 ? content[..300] + "..." : content)
            : "(no text)";
        var toolCallSummary = toolCalls != null
            ? string.Join(", ", toolCalls.Select(tc => $"{tc.Name}({Truncate(tc.ArgumentsJson, 200)})"))
            : "(no tool calls)";
        _logger.LogInformation("[Trace] LLM Request End: Agent={Agent} Content={Content} ToolCalls=[{ToolCalls}]",
            ctx.AgentId, contentPreview, toolCallSummary);
        return Task.CompletedTask;
    }

    public Task OnToolExecuteStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] Tool Start: {Tool} Args={Args}", ctx.ToolName, Truncate(ctx.ToolArguments, 500)); return Task.CompletedTask; }

    public Task OnToolExecuteEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] Tool End: {Tool} Result={Result}", ctx.ToolName, Truncate(ctx.ToolResult, 500)); return Task.CompletedTask; }

    private static string Truncate(string? value, int maxLength) =>
        value == null ? "(null)" : value.Length > maxLength ? value[..maxLength] + "..." : value;
}
