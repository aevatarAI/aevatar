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

    // ─── 上下文压缩生命周期 ───

    /// <summary>上下文压缩开始前。Items 中含 compression_reason、last_prompt_tokens、budget_limit。</summary>
    Task OnCompactStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;

    /// <summary>上下文压缩完成后。Items 中含 compacted_tool_results、truncated_messages、summarized。</summary>
    Task OnCompactEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;

    // ─── 工具审批生命周期 ───

    /// <summary>工具审批请求已发出。Items 中含 approval_request_id、approval_mode。</summary>
    Task OnToolApprovalRequestedAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;

    /// <summary>工具审批完成。Items 中含 approval_decision、approval_reason。</summary>
    Task OnToolApprovalCompletedAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;

    // ─── Post-Sampling（LLM 输出后、Tool 执行前） ───

    /// <summary>
    /// LLM 响应完成后、Tool 调用执行前的拦截点。
    /// 可用于输出安全过滤、结构化输出校验、Tool call 参数预审批。
    /// ctx.LLMResponse 包含完整 LLM 响应（含 tool calls）。
    /// Items["tool_call_count"] 包含待执行的 tool call 数量。
    /// 返回时可通过 Items["block_tool_calls"] = true 阻止 tool call 执行。
    /// </summary>
    Task OnPostSamplingAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;

    // ─── Tool 执行失败 ───

    /// <summary>
    /// Tool 执行抛出异常时触发。与 OnToolExecuteEnd 不同，专门处理失败场景。
    /// ctx.ToolName / ctx.ToolArguments / ctx.ToolCallId 包含失败的 tool 信息。
    /// Items["tool_error"] 包含异常对象。
    /// Items["tool_error_message"] 包含异常消息。
    /// 可通过 Items["retry"] = true 建议重试。
    /// </summary>
    Task OnToolExecuteFailureAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;

    // ─── 通知事件 ───

    /// <summary>
    /// 通用通知事件分发。用于 task 完成、agent 空闲、预算告警等。
    /// Items["notification_type"] 标识通知类型（如 "budget_warning"、"task_completed"）。
    /// Items["notification_payload"] 包含通知载荷。
    /// </summary>
    Task OnNotificationAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;

    // ─── Turn 完成/失败 ───

    /// <summary>
    /// Agent 轮次正常完成（最终响应即将返回）。
    /// ctx.Items["final_content"] 包含最终文本内容。
    /// ctx.Items["total_rounds"] 包含本次执行的 tool calling 轮数。
    /// 可用于最终响应过滤、完成日志。
    /// </summary>
    Task OnStopAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Agent 轮次因 API 错误或异常而终止。
    /// Items["error"] 包含异常对象。
    /// Items["error_message"] 包含错误消息。
    /// Items["error_phase"] 标识失败阶段（"llm_call"、"tool_execution" 等）。
    /// </summary>
    Task OnStopFailureAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;
}
