// ─────────────────────────────────────────────────────────────
// MEAILLMProviderFactory — MEAI 提供者工厂
//
// 支持注册多个 provider（openai、deepseek、azure 等），
// 按名字选择。每个 provider 由一个 IChatClient 支撑。
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Aevatar.AI.Abstractions.LLMProviders;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.LLMProviders.MEAI;

/// <summary>
/// 基于 MEAI 的 LLM Provider 工厂。
/// 支持注册多个命名 provider，按名字获取。
/// </summary>
public sealed class MEAILLMProviderFactory : ILLMProviderFactory, IMEAILLMProviderRegistry
{
    private readonly ConcurrentDictionary<string, MEAILLMProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private string _defaultName = "openai";

    /// <summary>
    /// 注册一个命名的 LLM Provider。
    /// </summary>
    /// <param name="name">名称（如 "openai", "deepseek", "azure"）。</param>
    /// <param name="client">MEAI 的 IChatClient。</param>
    /// <param name="logger">日志记录器。</param>
    public IMEAILLMProviderRegistry Register(string name, IChatClient client, ILogger? logger = null)
    {
        _providers[name] = new MEAILLMProvider(name, client, logger);
        return this;
    }

    /// <summary>设置默认 provider 名称。</summary>
    public IMEAILLMProviderRegistry SetDefault(string name)
    {
        _defaultName = name;
        return this;
    }

    /// <inheritdoc />
    public ILLMProvider GetProvider(string name) =>
        _providers.GetValueOrDefault(name)
        ?? throw new InvalidOperationException($"LLM Provider '{name}' 未注册。可用: {string.Join(", ", _providers.Keys)}");

    /// <inheritdoc />
    public ILLMProvider GetDefault() => GetProvider(_defaultName);

    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableProviders() => _providers.Keys.ToList();

    // ─── 快捷工厂方法 ───

    /// <summary>
    /// 从 OpenAI API Key 创建并注册 provider。
    /// 支持 OpenAI 及兼容 API（DeepSeek、Moonshot 等，通过 baseUrl 配置）。
    /// </summary>
    /// <param name="name">Provider 名称。</param>
    /// <param name="model">模型名称。</param>
    /// <param name="apiKey">API Key。</param>
    /// <param name="baseUrl">API 基地址（null 则用 OpenAI 默认）。</param>
    /// <param name="logger">日志记录器。</param>
    public IMEAILLMProviderRegistry RegisterOpenAI(
        string name, string model, string apiKey,
        string? baseUrl = null, ILogger? logger = null)
    {
        OpenAI.Chat.ChatClient chatClient;

        if (baseUrl != null)
        {
            // 兼容 API（DeepSeek、Moonshot 等）
            var options = new OpenAI.OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
            var openAiClient = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), options);
            chatClient = openAiClient.GetChatClient(model);
        }
        else
        {
            // 标准 OpenAI
            chatClient = new OpenAI.Chat.ChatClient(model, apiKey);
        }

        var meaiClient = chatClient.AsIChatClient();
        return Register(name, meaiClient, logger);
    }
}
