// ─────────────────────────────────────────────────────────────
// ServiceCollectionExtensions — MEAI DI 注册
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
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
    ///     .RegisterOpenAI("openai", "gpt-5.4", openaiKey)
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

        services.AddSingleton<ILLMProviderFactory>(serviceProvider =>
        {
            var factory = new MEAILLMProviderFactory(new ServiceProviderAgentToolExecutionPort(serviceProvider));
            configure(factory);
            return factory;
        });
        return services;
    }

    private sealed class ServiceProviderAgentToolExecutionPort(IServiceProvider serviceProvider) : IAgentToolExecutionPort
    {
        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default) =>
            serviceProvider.GetRequiredService<IAgentToolExecutionPort>().ExecuteAsync(request, ct);
    }
}
