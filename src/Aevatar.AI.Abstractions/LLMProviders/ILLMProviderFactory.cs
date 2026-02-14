// ─────────────────────────────────────────────────────────────
// ILLMProviderFactory — LLM 提供者工厂接口
// 按名称获取 Provider，或取默认 Provider
// ─────────────────────────────────────────────────────────────

namespace Aevatar.AI.Abstractions.LLMProviders;

/// <summary>LLM 提供者工厂。负责创建和解析不同 Provider 实例。</summary>
public interface ILLMProviderFactory
{
    /// <summary>按名称获取指定的 LLM 提供者。</summary>
    /// <param name="name">Provider 名称。</param>
    /// <returns>对应的 ILLMProvider 实例。</returns>
    ILLMProvider GetProvider(string name);

    /// <summary>获取默认的 LLM 提供者。</summary>
    /// <returns>默认 ILLMProvider 实例。</returns>
    ILLMProvider GetDefault();

    /// <summary>获取所有可用的 Provider 名称列表。</summary>
    /// <returns>Provider 名称只读列表。</returns>
    IReadOnlyList<string> GetAvailableProviders();
}
