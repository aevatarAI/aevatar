// ─────────────────────────────────────────────────────────────
// AIGAgentExecutionHookContext — AI 层 Hook 上下文
// 继承 GAgentExecutionHookContext，增加 LLM / Tool 字段
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.Hooks;

namespace Aevatar.AI.Core.Hooks;

/// <summary>
/// AI 层 Hook 上下文。继承 Foundation 的 GAgentExecutionHookContext，
/// 增加 LLM Request/Response、Tool Name/Args/Result 等字段。
/// </summary>
public sealed class AIGAgentExecutionHookContext : GAgentExecutionHookContext
{
    // ─── LLM 上下文 ───

    /// <summary>LLM 请求对象（可能是 LLMRequest）。</summary>
    public object? LLMRequest { get; set; }

    /// <summary>LLM 响应对象（可能是 LLMResponse）。</summary>
    public object? LLMResponse { get; set; }

    // ─── Tool 上下文 ───

    /// <summary>工具名称。</summary>
    public string? ToolName { get; set; }

    /// <summary>工具参数 JSON。</summary>
    public string? ToolArguments { get; set; }

    /// <summary>工具执行结果。</summary>
    public string? ToolResult { get; set; }

    /// <summary>Tool Call ID。</summary>
    public string? ToolCallId { get; set; }
}
