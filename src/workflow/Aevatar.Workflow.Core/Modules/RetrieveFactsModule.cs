// ─────────────────────────────────────────────────────────────
// RetrieveFactsModule — 事实检索模块
// 从 input 中的事实列表中检索 top-k 相关事实
// 简化实现：基于关键词匹配
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>事实检索模块。处理 type=retrieve_facts 的步骤。</summary>
public sealed class RetrieveFactsModule : IEventModule<IWorkflowExecutionContext>
{
    public string Name => "retrieve_facts";
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (request.StepType != "retrieve_facts") return;

        var topK = WorkflowParameterValueParser.GetBoundedInt(request.Parameters, 5, 1, 100, "top_k", "k");
        var query = WorkflowParameterValueParser.GetString(request.Parameters, string.Empty, "query", "keywords");
        var delimiter = WorkflowParameterValueParser.NormalizeEscapedText(
            WorkflowParameterValueParser.GetString(request.Parameters, "\n", "delimiter", "separator"),
            "\n");

        // input 包含事实列表（每行一个事实或 --- 分隔）
        var facts = WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray(request.Input, delimiter);
        if (facts.Length == 0 && request.Parameters.TryGetValue("facts", out var factsRaw))
            facts = WorkflowParameterValueParser.ParseStringList(factsRaw).ToArray();

        // 简化检索：按包含 query 关键词排序
        var queryWords = query.Split([' ', ',', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
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
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true, Output = output,
        }, TopologyAudience.Self, ct);
    }
}
