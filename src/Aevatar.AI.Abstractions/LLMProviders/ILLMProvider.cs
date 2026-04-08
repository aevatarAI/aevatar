// ─────────────────────────────────────────────────────────────
// ILLMProvider — 大语言模型提供者接口
// 定义 LLM 调用契约：同步 Chat 与流式 ChatStream
// ─────────────────────────────────────────────────────────────

namespace Aevatar.AI.Abstractions.LLMProviders;

/// <summary>大语言模型提供者接口。封装 Chat 与流式 ChatStream 调用。</summary>
public interface ILLMProvider
{
    /// <summary>提供者名称，用于从 Factory 中按名获取。</summary>
    string Name { get; }

    /// <summary>提供者能力描述。用于多模态请求分发与 failover 兼容性校验。</summary>
    LLMProviderCapabilities Capabilities => LLMProviderCapabilities.TextOnly;

    /// <summary>同步 Chat 调用。返回完整响应内容与 tool_calls。</summary>
    /// <param name="request">LLM 请求，包含消息、工具、模型参数等。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>LLM 响应，包含文本内容、工具调用、Token 用量等。</returns>
    Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default);

    /// <summary>流式 Chat 调用。按 chunk 返回文本增量与工具调用增量。</summary>
    /// <param name="request">LLM 请求。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>LLM 流式 chunk 的异步序列。</returns>
    IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(LLMRequest request, CancellationToken ct = default);
}
