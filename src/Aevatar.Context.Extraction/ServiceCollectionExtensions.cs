using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Context.Extraction;

/// <summary>
/// Context Extraction 模块的 DI 注册扩展。
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 L0/L1 生成器和语义处理器。
    /// 依赖 ILLMProviderFactory 和 IContextStore 已注册。
    /// </summary>
    public static IServiceCollection AddContextExtraction(this IServiceCollection services)
    {
        services.TryAddSingleton<IContextLayerGenerator, LLMContextLayerGenerator>();
        services.TryAddSingleton<SemanticProcessor>();
        return services;
    }
}
