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
        // Refactor (iter15/cluster-024):
        //   Old pattern: offline callers duplicated StringBuilder loops after direct provider.ChatAsync removal.
        //   New principle: ChatStreamAsync remains the only authoritative AI executor, with one narrow text aggregation adapter for offline consumers.
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
        // Refactor (iter15/cluster-024):
        //   Old pattern: ToolCallLoop treated provider.ChatAsync as a second authoritative LLM response path.
        //   New principle: stream-derived LLMResponse aggregation preserves content, reasoning, tool calls, usage, and finish reason from provider.ChatStreamAsync.
        var content = new StringBuilder();
        var reasoningContent = new StringBuilder();
        var toolCalls = new StreamingToolCallAccumulator();
        TokenUsage? usage = null;
        string? finishReason = null;

        await foreach (var chunk in provider.ChatStreamAsync(request, ct).WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
                content.Append(chunk.DeltaContent);

            if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent))
                reasoningContent.Append(chunk.DeltaReasoningContent);

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
            ToolCalls = finalToolCalls.Count > 0 ? finalToolCalls : null,
            Usage = usage,
            FinishReason = finishReason,
        };
    }
}
