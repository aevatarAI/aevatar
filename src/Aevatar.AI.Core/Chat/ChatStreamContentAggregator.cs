using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.AI.Core.Chat;

/// <summary>Aggregates authoritative chat streams for explicit offline text adapters.</summary>
public static class ChatStreamContentAggregator
{
    public static async Task<string?> AggregateContentAsync(
        IAsyncEnumerable<LLMStreamChunk> stream,
        bool emptyAsNull = true,
        CancellationToken ct = default)
    {
        // Refactor (iter18/cluster-001):
        //   Old pattern: ILLMProvider 仍暴露 ChatAsync 非流式入口,provider/failover 可绕过流式链路
        //   New principle: Provider contract 只暴露 ChatStreamAsync;非流式聚合用现有 ChatStreamContentAggregator;无新 offline adapter
        var content = new StringBuilder();
        await foreach (var chunk in stream.WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
                content.Append(chunk.DeltaContent);
        }

        return content.Length > 0 || !emptyAsNull ? content.ToString() : null;
    }

    public static async Task<LLMResponse> AggregateResponseAsync(
        ILLMProvider provider,
        LLMRequest request,
        CancellationToken ct = default)
    {
        // Refactor (iter18/cluster-001):
        //   Old pattern: ILLMProvider 仍暴露 ChatAsync 非流式入口,provider/failover 可绕过流式链路
        //   New principle: Provider contract 只暴露 ChatStreamAsync;非流式聚合用现有 ChatStreamContentAggregator;无新 offline adapter
        // New pattern: offline aggregation consumes ChatStreamAsync; provider execution remains stream-first.
        var content = new StringBuilder();
        var reasoningContent = new StringBuilder();
        List<ContentPart>? contentParts = null;
        var toolCalls = new StreamingToolCallAccumulator();
        TokenUsage? usage = null;
        string? finishReason = null;

        await foreach (var chunk in provider.ChatStreamAsync(request, ct).WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
                content.Append(chunk.DeltaContent);

            if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent))
                reasoningContent.Append(chunk.DeltaReasoningContent);

            if (chunk.DeltaContentPart != null)
            {
                contentParts ??= [];
                contentParts.Add(chunk.DeltaContentPart);
            }

            if (chunk.DeltaToolCall != null)
                toolCalls.TrackDelta(chunk.DeltaToolCall);

            if (chunk.Usage != null)
                usage = chunk.Usage;

            if (chunk.FinishReason != null)
                finishReason = chunk.FinishReason;
        }

        var finalToolCalls = toolCalls.BuildToolCalls();
        return new LLMResponse
        {
            Content = content.Length > 0 ? content.ToString() : null,
            ReasoningContent = reasoningContent.Length > 0 ? reasoningContent.ToString() : null,
            ContentParts = contentParts,
            ToolCalls = finalToolCalls.Count > 0 ? finalToolCalls : null,
            Usage = usage,
            FinishReason = finishReason,
        };
    }
}
