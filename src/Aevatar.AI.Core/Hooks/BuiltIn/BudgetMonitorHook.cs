// ─────────────────────────────────────────────────────────────
// BudgetMonitorHook — Token 预算监控
// LLM 调用前检查历史长度，超过阈值发出警告
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions.Hooks;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Core.Hooks.BuiltIn;

/// <summary>Token 预算监控。LLM 调用前检查历史消息数，LLM 调用后检查 token 用量。</summary>
public sealed class BudgetMonitorHook : IAIGAgentExecutionHook
{
    private readonly ILogger _logger;

    /// <summary>警告阈值（历史消息数）。</summary>
    public int WarningThreshold { get; set; } = 50;

    /// <summary>Prompt token 警告阈值。超过此值时发出警告。</summary>
    public int TokenWarningThreshold { get; set; } = 50_000;

    public BudgetMonitorHook(ILogger? logger = null) =>
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public string Name => "budget_monitor";
    public int Priority => -500; // 较高优先级

    public Task OnLLMRequestStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    {
        if (ctx.Items.TryGetValue("history_count", out var val) &&
            val is int count && count > WarningThreshold)
            _logger.LogWarning("[Budget] 历史消息数 {Count} 超过阈值 {Threshold}", count, WarningThreshold);
        return Task.CompletedTask;
    }

    public Task OnLLMRequestEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    {
        if (ctx.LLMResponse is LLMResponse { Usage: not null } response
            && response.Usage.PromptTokens > TokenWarningThreshold)
        {
            _logger.LogWarning(
                "[Budget] Prompt tokens {Tokens} 超过警告阈值 {Threshold}（completion={Completion}，total={Total}）",
                response.Usage.PromptTokens,
                TokenWarningThreshold,
                response.Usage.CompletionTokens,
                response.Usage.TotalTokens);
        }
        return Task.CompletedTask;
    }
}
