// ─────────────────────────────────────────────────────────────
// AevatarConfigLoader — 加载 ~/.aevatar/ 下的配置文件
//
// 将 config.json + secrets.json + mcp.json 合并为
// IConfiguration，供其他项目通过 DI 使用。
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.Configuration;

namespace Aevatar.Config;

/// <summary>
/// 加载 ~/.aevatar/ 下的配置文件到 IConfiguration。
/// 优先级：secrets.json > config.json（secrets 覆盖同名 key）。
/// </summary>
public static class AevatarConfigLoader
{
    /// <summary>
    /// 将 ~/.aevatar/ 下的配置文件添加到 IConfigurationBuilder。
    /// </summary>
    /// <param name="builder">配置构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static IConfigurationBuilder AddAevatarConfig(this IConfigurationBuilder builder)
    {
        // 确保目录存在
        AevatarPaths.EnsureDirectories();

        // config.json — 非敏感配置（低优先级）
        builder.AddJsonFile(AevatarPaths.ConfigJson, optional: true, reloadOnChange: true);

        // secrets.json — 敏感配置（高优先级，覆盖 config.json 的同名 key）
        builder.AddJsonFile(AevatarPaths.SecretsJson, optional: true, reloadOnChange: false);

        // mcp.json — MCP 服务器配置（Cursor 兼容格式）
        builder.AddJsonFile(AevatarPaths.McpJson, optional: true, reloadOnChange: true);

        // connectors.json — 命名连接器配置（MCP/HTTP/CLI）
        builder.AddJsonFile(AevatarPaths.ConnectorsJson, optional: true, reloadOnChange: true);

        // 环境变量（最高优先级，AEVATAR_ 前缀）
        builder.AddEnvironmentVariables("AEVATAR_");

        return builder;
    }
}
