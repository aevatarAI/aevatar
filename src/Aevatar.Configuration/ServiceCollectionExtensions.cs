// ─────────────────────────────────────────────────────────────
// ServiceCollectionExtensions — Aevatar Config DI 注册
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.Credentials;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Configuration;

/// <summary>Aevatar Config 的 DI 注册扩展。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Aevatar 配置服务。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="allowLocalFileStore">
    /// 是否允许使用本地文件 secrets store（<see cref="AevatarSecretsStore"/>，
    /// 读写 <c>~/.aevatar/secrets.json</c>）。
    /// <para>
    /// <c>true</c>（默认）：注册 <see cref="AevatarSecretsStore"/> 并自动创建
    /// <c>~/.aevatar/</c> 目录结构，沿用历史行为。
    /// </para>
    /// <para>
    /// <c>false</c>：注册只读的 <see cref="EnvironmentSecretsStore"/>，
    /// 仅从 <see cref="IConfiguration"/>（含 <c>AEVATAR_</c> 环境变量）读取，
    /// <c>Set</c>/<c>Remove</c> 直接抛 <see cref="InvalidOperationException"/>。
    /// 不创建本地目录。生产/mainnet 入口必须传 <c>false</c>。
    /// </para>
    /// <para>
    /// <b>前置条件（仅 <c>false</c> 路径）</b>：DI 容器中必须已注册
    /// <see cref="IConfiguration"/>，<see cref="EnvironmentSecretsStore"/>
    /// 通过构造函数注入它。<see cref="AddAevatarConfig"/>
    /// 自身不注册 <see cref="IConfiguration"/>；通过
    /// <c>WebApplicationBuilder</c> 或 <c>HostBuilder</c> 调用本扩展时框架已经
    /// 注册好。裸 <see cref="IServiceCollection"/> 调用方需自行
    /// <c>services.AddSingleton&lt;IConfiguration&gt;(...)</c>。
    /// </para>
    /// </param>
    public static IServiceCollection AddAevatarConfig(
        this IServiceCollection services,
        bool allowLocalFileStore = true)
    {
        if (allowLocalFileStore)
        {
            AevatarPaths.EnsureDirectories();
            services.TryAddSingleton<IAevatarSecretsStore, AevatarSecretsStore>();
            services.TryAddSingleton<AevatarSecretsStore>();
        }
        else
        {
            services.TryAddSingleton<IAevatarSecretsStore, EnvironmentSecretsStore>();
            services.TryAddSingleton<EnvironmentSecretsStore>();
        }
        services.TryAddSingleton<ICredentialProvider, SecretsStoreCredentialProvider>();
        services.TryAddSingleton<SecretsStoreCredentialProvider>();
        return services;
    }
}
