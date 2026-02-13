// ─────────────────────────────────────────────────────────────
// ServiceCollectionExtensions — Aevatar Config DI 注册
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Config;

/// <summary>Aevatar Config 的 DI 注册扩展。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Aevatar 配置服务。自动创建 ~/.aevatar/ 目录结构。
    /// </summary>
    public static IServiceCollection AddAevatarConfig(this IServiceCollection services)
    {
        AevatarPaths.EnsureDirectories();
        services.TryAddSingleton<AevatarSecretsStore>();
        return services;
    }
}
