// ─────────────────────────────────────────────────────────────
// ServiceCollectionExtensions — LlmTornado DI 注册
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.LLM;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.LLMTornado;

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
        Action<TornadoLLMProviderFactory> configure)
    {
        var factory = new TornadoLLMProviderFactory();
        configure(factory);
        services.TryAddSingleton<ILLMProviderFactory>(factory);
        return services;
    }
}
