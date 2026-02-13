// ─────────────────────────────────────────────────────────────
// ServiceCollectionExtensions — MEAI DI 注册
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.LLM;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.MEAI;

/// <summary>MEAI LLM Provider 的 DI 注册扩展。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 MEAI LLM Provider Factory 并配置 providers。
    /// </summary>
    /// <example>
    /// services.AddMEAIProviders(factory => factory
    ///     .RegisterOpenAI("openai", "gpt-4o-mini", openaiKey)
    ///     .RegisterOpenAI("deepseek", "deepseek-chat", deepseekKey,
    ///         baseUrl: "https://api.deepseek.com/v1")
    ///     .SetDefault("deepseek"));
    /// </example>
    public static IServiceCollection AddMEAIProviders(
        this IServiceCollection services,
        Action<MEAILLMProviderFactory> configure)
    {
        var factory = new MEAILLMProviderFactory();
        configure(factory);
        services.TryAddSingleton<ILLMProviderFactory>(factory);
        return services;
    }
}
