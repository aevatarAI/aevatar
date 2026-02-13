// ─────────────────────────────────────────────────────────────
// BudgetMonitorHook — Token 预算监控
// LLM 调用前检查历史长度，超过阈值发出警告
// ─────────────────────────────────────────────────────────────

using Aevatar.Hooks;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Hooks.BuiltIn;

/// <summary>Token 预算监控。LLM 调用前检查历史消息数。</summary>
public sealed class BudgetMonitorHook : IAgentHook
{
    private readonly ILogger _logger;

    /// <summary>警告阈值（历史消息数）。</summary>
    public int WarningThreshold { get; set; } = 50;

    public BudgetMonitorHook(ILogger? logger = null) =>
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public string Name => "budget_monitor";
    public int Priority => -500; // 较高优先级

    public Task OnLLMRequestStartAsync(AIHookContext ctx, CancellationToken ct)
    {
        if (ctx.Metadata.TryGetValue("history_count", out var val) &&
            val is int count && count > WarningThreshold)
            _logger.LogWarning("[Budget] 历史消息数 {Count} 超过阈值 {Threshold}", count, WarningThreshold);
        return Task.CompletedTask;
    }
}
