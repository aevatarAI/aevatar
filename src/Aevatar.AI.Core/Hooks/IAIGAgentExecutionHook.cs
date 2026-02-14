// ─────────────────────────────────────────────────────────────
// IAIGAgentExecutionHook — AI 层 Hook 接口
//
// 继承 Foundation 的 IGAgentExecutionHook（事件处理器 hook 点），
// 增加 LLM 和 Tool 的 hook 点。
// 一个 hook 实现可以同时处理 Event Handler + LLM + Tool。
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.Hooks;

namespace Aevatar.AI.Core.Hooks;

/// <summary>
/// AI 层 Hook 接口。继承 Foundation 的 IGAgentExecutionHook，
/// 增加 LLM 调用和 Tool 执行的 hook 点。
/// </summary>
public interface IAIGAgentExecutionHook : IGAgentExecutionHook
{
    // ─── LLM 生命周期 ───

    /// <summary>LLM 调用前。</summary>
    Task OnLLMRequestStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;

    /// <summary>LLM 调用后。</summary>
    Task OnLLMRequestEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;

    // ─── Tool 生命周期 ───

    /// <summary>Tool 执行前。</summary>
    Task OnToolExecuteStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Tool 执行后。</summary>
    Task OnToolExecuteEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;

    // ─── 会话生命周期 ───

    /// <summary>会话开始。</summary>
    Task OnSessionStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;

    /// <summary>会话结束。</summary>
    Task OnSessionEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;
}
