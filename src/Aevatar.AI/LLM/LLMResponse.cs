// ─────────────────────────────────────────────────────────────
// LLMResponse / LLMStreamChunk / TokenUsage — LLM 响应与流式模型
// 封装 Chat API 返回的文本、tool_calls、Token 用量
// ─────────────────────────────────────────────────────────────

namespace Aevatar.AI.LLM;

/// <summary>LLM 同步 Chat 响应 DTO。</summary>
public sealed class LLMResponse
{
    /// <summary>LLM 生成的文本内容。</summary>
    public string? Content { get; init; }

    /// <summary>LLM 请求调用的工具列表。</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>是否存在 tool_calls（即需要执行工具）。</summary>
    public bool HasToolCalls => ToolCalls is { Count: > 0 };

    /// <summary>Token 用量统计。</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>完成原因，如 stop / tool_calls / length。</summary>
    public string? FinishReason { get; init; }
}

/// <summary>LLM 流式 Chat 单块数据。</summary>
public sealed class LLMStreamChunk
{
    /// <summary>本块增量文本内容。</summary>
    public string? DeltaContent { get; init; }

    /// <summary>本块增量 tool_call（若存在）。</summary>
    public ToolCall? DeltaToolCall { get; init; }

    /// <summary>是否为最后一块。</summary>
    public bool IsLast { get; init; }

    /// <summary>Token 用量（通常最后一块才有）。</summary>
    public TokenUsage? Usage { get; init; }
}

/// <summary>Token 用量统计。</summary>
/// <param name="PromptTokens">Prompt 消耗的 Token 数。</param>
/// <param name="CompletionTokens">生成消耗的 Token 数。</param>
/// <param name="TotalTokens">总 Token 数。</param>
public sealed record TokenUsage(int PromptTokens, int CompletionTokens, int TotalTokens);
