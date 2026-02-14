// ─────────────────────────────────────────────────────────────
// VoteConsensusModule — 投票共识模块
// 收集多个候选结果，通过投票选出最佳（简化实现：选最长）
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflows.Core.Modules;

/// <summary>投票共识模块。处理 type=vote 的步骤。</summary>
public sealed class VoteConsensusModule : IEventModule
{
    public string Name => "vote_consensus";
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var evt = envelope.Payload!.Unpack<StepRequestEvent>();
        if (evt.StepType != "vote") return;

        var candidates = evt.Input.Split("\n---\n", StringSplitOptions.RemoveEmptyEntries);
        if (candidates.Length == 0)
        {
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = evt.StepId, RunId = evt.RunId, Success = false,
                Error = "投票步骤没有候选结果",
            }, EventDirection.Self, ct);
            return;
        }

        // 简化投票：选最长的（实际应用应用 LLM 评估）
        var winner = candidates.OrderByDescending(c => c.Length).First();
        ctx.Logger.LogInformation("投票步骤 {StepId}: {Count} 个候选，已选出最佳", evt.StepId, candidates.Length);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = evt.StepId, RunId = evt.RunId, Success = true, Output = winner,
        }, EventDirection.Self, ct);
    }
}
