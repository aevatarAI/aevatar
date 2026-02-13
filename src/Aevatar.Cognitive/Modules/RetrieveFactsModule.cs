// ─────────────────────────────────────────────────────────────
// RetrieveFactsModule — 事实检索模块
// 从 input 中的事实列表中检索 top-k 相关事实
// 简化实现：基于关键词匹配
// ─────────────────────────────────────────────────────────────

using Aevatar;
using Aevatar.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Cognitive.Modules;

/// <summary>事实检索模块。处理 type=retrieve_facts 的步骤。</summary>
public sealed class RetrieveFactsModule : IEventModule
{
    public string Name => "retrieve_facts";
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.TypeUrl?.Contains("StepRequestEvent") == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (request.StepType != "retrieve_facts") return;

        var topK = int.TryParse(request.Parameters.GetValueOrDefault("top_k", "5"), out var k) ? k : 5;
        var query = request.Parameters.GetValueOrDefault("query", "");

        // input 包含事实列表（每行一个事实或 --- 分隔）
        var facts = request.Input.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // 简化检索：按包含 query 关键词排序
        var queryWords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var scored = facts
            .Select(f => (Fact: f, Score: queryWords.Count(w => f.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Fact);

        var output = string.Join("\n", scored);

        ctx.Logger.LogInformation("RetrieveFacts {StepId}: query='{Query}', 总{Total}条 → top-{K}",
            request.StepId, query, facts.Length, topK);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId, RunId = request.RunId,
            Success = true, Output = output,
        }, EventDirection.Self, ct);
    }
}
