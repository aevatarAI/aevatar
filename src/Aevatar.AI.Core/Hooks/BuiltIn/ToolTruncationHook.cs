// ─────────────────────────────────────────────────────────────
// ToolTruncationHook — 截断过大的工具输出
// 防止 context 溢出
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.Hooks;

namespace Aevatar.AI.Core.Hooks.BuiltIn;

/// <summary>截断过大的工具输出，防止 context window 溢出。</summary>
public sealed class ToolTruncationHook : IAIGAgentExecutionHook
{
    /// <summary>最大输出长度（字符数）。</summary>
    public int MaxOutputLength { get; set; } = 8000;

    public string Name => "tool_truncation";
    public int Priority => 100;

    public Task OnToolExecuteEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    {
        if (ctx.ToolResult != null && ctx.ToolResult.Length > MaxOutputLength)
            ctx.ToolResult = ctx.ToolResult[..MaxOutputLength] + "\n...[truncated]";
        return Task.CompletedTask;
    }
}
