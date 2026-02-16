using Aevatar.Context.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Context.Retrieval;

/// <summary>
/// Context Retrieval 模块的 DI 注册扩展。
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册上下文检索服务（向量索引 + 意图分析 + 层级检索器）。
    /// 依赖 ILLMProviderFactory 和 IEmbeddingGenerator 已注册。
    /// </summary>
    public static IServiceCollection AddContextRetrieval(this IServiceCollection services)
    {
        services.TryAddSingleton<IContextVectorIndex, LocalVectorIndex>();
        services.TryAddSingleton<IntentAnalyzer>();
        services.TryAddSingleton<IContextRetriever, HierarchicalRetriever>();
        return services;
    }
}
