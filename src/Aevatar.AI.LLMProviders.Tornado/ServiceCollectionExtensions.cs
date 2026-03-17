// ─────────────────────────────────────────────────────────────
// ServiceCollectionExtensions — LlmTornado DI 注册
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.LLMProviders;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;

namespace Aevatar.AI.LLMProviders.Tornado;

/// <summary>LlmTornado Provider 的 DI 注册扩展。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 LlmTornado LLM Provider Factory。
    /// </summary>
    /// <example>
    /// services.AddTornadoProviders(factory => factory
    ///     .Register("anthropic", LLmProviders.Anthropic, claudeKey, "claude-sonnet-4-20250514")
    ///     .Register("google", LLmProviders.Google, geminiKey, "gemini-2.0-flash")
    ///     .SetDefault("anthropic"));
    /// </example>
    public static IServiceCollection AddTornadoProviders(
        this IServiceCollection services,
        Action<ITornadoLLMProviderRegistry> configure)
    {
        if (services.Any(x => x.ServiceType == typeof(ILLMProviderFactory)))
        {
            throw new InvalidOperationException(
                "ILLMProviderFactory is already registered. Multiple factory implementations are not supported in the same IServiceCollection.");
        }

        var factory = new TornadoLLMProviderFactory();
        configure(factory);
        services.AddSingleton<ILLMProviderFactory>(factory);
        return services;
    }
}
