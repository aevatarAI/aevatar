using Aevatar.Configuration;
using Aevatar.Context.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Context.Core;

/// <summary>
/// Context Database 的 DI 注册扩展。
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册基于本地文件系统的 Context Store。
    /// 自动确保 ~/.aevatar/ 下的上下文目录已创建。
    /// </summary>
    public static IServiceCollection AddContextStore(this IServiceCollection services)
    {
        AevatarPaths.EnsureContextDirectories();

        services.TryAddSingleton<AevatarUriPhysicalMapper>();
        services.TryAddSingleton<IContextStore, LocalFileContextStore>();
        return services;
    }

    /// <summary>
    /// 注册基于内存的 Context Store（用于测试）。
    /// </summary>
    public static IServiceCollection AddInMemoryContextStore(this IServiceCollection services)
    {
        services.TryAddSingleton<IContextStore, InMemoryContextStore>();
        return services;
    }
}
