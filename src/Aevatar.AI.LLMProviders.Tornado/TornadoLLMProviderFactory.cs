// ─────────────────────────────────────────────────────────────
// TornadoLLMProviderFactory — LlmTornado 提供者工厂
// 支持注册多个 provider（openai / anthropic / google 等）
// ─────────────────────────────────────────────────────────────

using System.Collections.Immutable;
using Aevatar.AI.Abstractions.LLMProviders;
using LlmTornado;
using LlmTornado.Code;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.LLMProviders.Tornado;

/// <summary>
/// 基于 LlmTornado 的 LLM Provider 工厂。
/// 支持注册多个命名 provider。
/// Startup-initialized, read-heavy: uses ImmutableDictionary for lock-free reads.
/// </summary>
public sealed class TornadoLLMProviderFactory : ILLMProviderFactory, ITornadoLLMProviderRegistry
{
    private ImmutableDictionary<string, TornadoLLMProvider> _providers =
        ImmutableDictionary<string, TornadoLLMProvider>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

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
        var provider = new TornadoLLMProvider(name, api, model, logger);
        ImmutableInterlocked.AddOrUpdate(ref _providers, name, provider, (_, _) => provider);
        if (string.IsNullOrEmpty(Volatile.Read(ref _defaultName))) Volatile.Write(ref _defaultName, name);
        return this;
    }

    /// <summary>注册 OpenAI 兼容 provider（含自定义 baseUrl）。</summary>
    public ITornadoLLMProviderRegistry RegisterOpenAICompatible(
        string name, string apiKey, string model, string? baseUrl = null, ILogger? logger = null)
    {
        TornadoApi api;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint))
                throw new ArgumentException($"Invalid baseUrl '{baseUrl}'. Expected an absolute URI.", nameof(baseUrl));

            api = new TornadoApi(endpoint, apiKey, LLmProviders.OpenAi);
        }
        else
        {
            api = new TornadoApi(LLmProviders.OpenAi, apiKey);
        }

        var provider = new TornadoLLMProvider(name, api, model, logger);
        ImmutableInterlocked.AddOrUpdate(ref _providers, name, provider, (_, _) => provider);
        if (string.IsNullOrEmpty(Volatile.Read(ref _defaultName))) Volatile.Write(ref _defaultName, name);
        return this;
    }

    /// <summary>设置默认 provider。</summary>
    public ITornadoLLMProviderRegistry SetDefault(string name) { Volatile.Write(ref _defaultName, name); return this; }

    /// <inheritdoc />
    public ILLMProvider GetProvider(string name) =>
        _providers.TryGetValue(name, out var provider)
        ? provider
        : throw new InvalidOperationException($"Tornado Provider '{name}' 未注册");

    /// <inheritdoc />
    public ILLMProvider GetDefault() => GetProvider(Volatile.Read(ref _defaultName));

    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableProviders() => _providers.Keys.ToList();
}
