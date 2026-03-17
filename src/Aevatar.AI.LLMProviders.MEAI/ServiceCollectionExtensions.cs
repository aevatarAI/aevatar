// ─────────────────────────────────────────────────────────────
// ServiceCollectionExtensions — MEAI DI 注册
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.LLMProviders;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;

namespace Aevatar.AI.LLMProviders.MEAI;

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
        Action<IMEAILLMProviderRegistry> configure)
    {
        if (services.Any(x => x.ServiceType == typeof(ILLMProviderFactory)))
        {
            throw new InvalidOperationException(
                "ILLMProviderFactory is already registered. Multiple factory implementations are not supported in the same IServiceCollection.");
        }

        var factory = new MEAILLMProviderFactory();
        configure(factory);
        services.AddSingleton<ILLMProviderFactory>(factory);
        return services;
    }
}
