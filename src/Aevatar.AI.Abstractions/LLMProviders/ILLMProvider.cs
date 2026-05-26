// ─────────────────────────────────────────────────────────────
// ILLMProvider — 大语言模型提供者接口
// 定义 LLM 调用契约：provider execution is stream-only.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.AI.Abstractions.LLMProviders;

/// <summary>大语言模型提供者接口。封装流式 ChatStream 调用。</summary>
public interface ILLMProvider
{
    /// <summary>提供者名称，用于从 Factory 中按名获取。</summary>
    string Name { get; }

    /// <summary>提供者能力描述。用于多模态请求分发与 failover 兼容性校验。</summary>
    LLMProviderCapabilities Capabilities => LLMProviderCapabilities.TextOnly;

    /// <summary>流式 Chat 调用。按 chunk 返回文本增量与工具调用增量。</summary>
    // Refactor (iter18/cluster-001):
    //   Old pattern: ILLMProvider 仍暴露 ChatAsync 非流式入口,provider/failover 可绕过流式链路
    //   New principle: Provider contract 只暴露 ChatStreamAsync;非流式聚合用现有 ChatStreamContentAggregator;无新 offline adapter
    /// <param name="request">LLM 请求。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>LLM 流式 chunk 的异步序列。</returns>
    IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(LLMRequest request, CancellationToken ct = default);
}
