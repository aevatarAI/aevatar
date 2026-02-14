// ─────────────────────────────────────────────────────────────
// TornadoLLMProviderFactory — LlmTornado 提供者工厂
// 支持注册多个 provider（openai / anthropic / google 等）
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Aevatar.AI.Abstractions.LLMProviders;
using LlmTornado;
using LlmTornado.Code;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.LLMProviders.Tornado;

/// <summary>
/// 基于 LlmTornado 的 LLM Provider 工厂。
/// 支持注册多个命名 provider。
/// </summary>
public sealed class TornadoLLMProviderFactory : ILLMProviderFactory, ITornadoLLMProviderRegistry
{
    private readonly ConcurrentDictionary<string, TornadoLLMProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private string _defaultName = "";

    /// <summary>
    /// 注册一个 provider。
    /// </summary>
    /// <param name="name">名称（如 "anthropic", "google"）。</param>
    /// <param name="providerType">LlmTornado 的 provider 类型。</param>
    /// <param name="apiKey">API Key。</param>
    /// <param name="model">模型名称。</param>
    /// <param name="logger">日志。</param>
    public ITornadoLLMProviderRegistry Register(
        string name, LLmProviders providerType, string apiKey, string model, ILogger? logger = null)
    {
        var api = new TornadoApi(providerType, apiKey);
        _providers[name] = new TornadoLLMProvider(name, api, model, logger);
        if (string.IsNullOrEmpty(_defaultName)) _defaultName = name;
        return this;
    }

    /// <summary>注册 OpenAI 兼容 provider（含自定义 baseUrl）。</summary>
    public ITornadoLLMProviderRegistry RegisterOpenAICompatible(
        string name, string apiKey, string model, string? baseUrl = null, ILogger? logger = null)
    {
        var api = new TornadoApi(LLmProviders.OpenAi, apiKey);
        _providers[name] = new TornadoLLMProvider(name, api, model, logger);
        if (string.IsNullOrEmpty(_defaultName)) _defaultName = name;
        return this;
    }

    /// <summary>设置默认 provider。</summary>
    public ITornadoLLMProviderRegistry SetDefault(string name) { _defaultName = name; return this; }

    /// <inheritdoc />
    public ILLMProvider GetProvider(string name) =>
        _providers.GetValueOrDefault(name)
        ?? throw new InvalidOperationException($"Tornado Provider '{name}' 未注册");

    /// <inheritdoc />
    public ILLMProvider GetDefault() => GetProvider(_defaultName);

    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableProviders() => _providers.Keys.ToList();
}
