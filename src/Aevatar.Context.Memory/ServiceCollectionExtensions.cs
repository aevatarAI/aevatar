using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Context.Memory;

/// <summary>
/// Context Memory 模块的 DI 注册扩展。
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册记忆提取、去重和写入服务。
    /// 依赖 ILLMProviderFactory、IContextStore、IContextVectorIndex、IEmbeddingGenerator 已注册。
    /// </summary>
    public static IServiceCollection AddContextMemory(this IServiceCollection services)
    {
        services.TryAddSingleton<IMemoryExtractor, LLMMemoryExtractor>();
        services.TryAddSingleton<MemoryDeduplicator>();
        services.TryAddSingleton<MemoryWriter>();
        return services;
    }
}
