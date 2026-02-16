// ─────────────────────────────────────────────────────────────
// AgentHookPipeline — AI 层 Hook 执行管线
//
// 按 Priority 排序执行 IAIGAgentExecutionHook。
// Best-effort：单个 hook 异常不阻塞主流程。
// 同时负责 Foundation 级（Event Handler）和 AI 级（LLM / Tool）的 hook 点。
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Core.Hooks;

/// <summary>
/// AI Hook 执行管线。按 Priority 排序，best-effort 执行。
/// </summary>
public sealed class AgentHookPipeline
{
    private readonly IAIGAgentExecutionHook[] _hooks;
    private readonly ILogger _logger;

    public AgentHookPipeline(IEnumerable<IAIGAgentExecutionHook> hooks, ILogger? logger = null)
    {
        _hooks = hooks.OrderBy(h => h.Priority).ThenBy(h => h.Name).ToArray();
        _logger = logger ?? NullLogger.Instance;
    }

    // ─── LLM 生命周期 ───

    /// <summary>LLM 调用前执行所有 hook。</summary>
    public Task RunLLMRequestStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) =>
        RunAll(h => h.OnLLMRequestStartAsync(ctx, ct), "OnLLMRequestStart");

    /// <summary>LLM 调用后执行所有 hook。</summary>
    public Task RunLLMRequestEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) =>
        RunAll(h => h.OnLLMRequestEndAsync(ctx, ct), "OnLLMRequestEnd");

    // ─── Tool 生命周期 ───

    /// <summary>Tool 执行前执行所有 hook。</summary>
    public Task RunToolExecuteStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) =>
        RunAll(h => h.OnToolExecuteStartAsync(ctx, ct), "OnToolExecuteStart");

    /// <summary>Tool 执行后执行所有 hook。</summary>
    public Task RunToolExecuteEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) =>
        RunAll(h => h.OnToolExecuteEndAsync(ctx, ct), "OnToolExecuteEnd");

    // ─── 会话生命周期 ───

    /// <summary>会话开始。</summary>
    public Task RunSessionStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) =>
        RunAll(h => h.OnSessionStartAsync(ctx, ct), "OnSessionStart");

    /// <summary>会话结束。</summary>
    public Task RunSessionEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) =>
        RunAll(h => h.OnSessionEndAsync(ctx, ct), "OnSessionEnd");

    // ─── 内部 ───

    private async Task RunAll(Func<IAIGAgentExecutionHook, Task> action, string phase)
    {
        foreach (var hook in _hooks)
        {
            try { await action(hook); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hook {Hook} 在 {Phase} 阶段失败（best-effort）", hook.Name, phase);
            }
        }
    }
}
