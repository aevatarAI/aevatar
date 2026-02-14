// ─────────────────────────────────────────────────────────────
// ExecutionTraceHook — 可观测性 Hook
// 在每个 hook 点位记录 trace 日志
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.Hooks;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Core.Hooks.BuiltIn;

/// <summary>在每个 hook 点位记录结构化 trace 日志。</summary>
public sealed class ExecutionTraceHook : IAgentHook
{
    private readonly ILogger _logger;

    public ExecutionTraceHook(ILogger? logger = null) =>
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public string Name => "execution_trace";
    public int Priority => -1000; // 最先执行

    // ─── Foundation 级 hook（Event Handler） ───

    public Task OnEventHandlerStartAsync(GAgentHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] EventHandler Start: Agent={Agent}, Handler={Handler}", ctx.AgentId, ctx.HandlerName); return Task.CompletedTask; }

    public Task OnEventHandlerEndAsync(GAgentHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] EventHandler End: Agent={Agent}, Handler={Handler}, Duration={Dur}ms", ctx.AgentId, ctx.HandlerName, ctx.Duration?.TotalMilliseconds); return Task.CompletedTask; }

    public Task OnErrorAsync(GAgentHookContext ctx, Exception ex, CancellationToken ct)
    { _logger.LogError(ex, "[Trace] Error: Agent={Agent}, Handler={Handler}", ctx.AgentId, ctx.HandlerName); return Task.CompletedTask; }

    // ─── AI 级 hook（LLM / Tool） ───

    public Task OnLLMRequestStartAsync(AIHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] LLM Request Start: Agent={Agent}", ctx.AgentId); return Task.CompletedTask; }

    public Task OnLLMRequestEndAsync(AIHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] LLM Request End: Agent={Agent}", ctx.AgentId); return Task.CompletedTask; }

    public Task OnToolExecuteStartAsync(AIHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] Tool Start: {Tool}", ctx.ToolName); return Task.CompletedTask; }

    public Task OnToolExecuteEndAsync(AIHookContext ctx, CancellationToken ct)
    { _logger.LogInformation("[Trace] Tool End: {Tool}", ctx.ToolName); return Task.CompletedTask; }
}
