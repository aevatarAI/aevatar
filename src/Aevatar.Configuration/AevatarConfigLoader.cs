// ─────────────────────────────────────────────────────────────
// AevatarConfigLoader — 加载 ~/.aevatar/ 下的配置文件
//
// 将 config.json + secrets.json + mcp.json 合并为
// IConfiguration，供其他项目通过 DI 使用。
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.Configuration;

namespace Aevatar.Configuration;

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
    /// <param name="allowLocalFileStore">
    /// 是否允许加载 <c>~/.aevatar/secrets.json</c>。生产/mainnet 入口必须传
    /// <c>false</c>，确保 secrets 仅来自部署平台注入的环境变量。其他 host 可
    /// 保留默认值。<c>config.json</c>、<c>mcp.json</c>、<c>connectors.json</c>
    /// 等非敏感文件不受此开关影响。
    /// </param>
    /// <returns>构建器（链式调用）。</returns>
    public static IConfigurationBuilder AddAevatarConfig(
        this IConfigurationBuilder builder,
        bool allowLocalFileStore = true)
    {
        // 确保目录存在
        AevatarPaths.EnsureDirectories();

        // config.json — 非敏感配置（低优先级）
        builder.AddJsonFile(AevatarPaths.ConfigJson, optional: true, reloadOnChange: true);

        // secrets.json — 敏感配置（高优先级，覆盖 config.json 的同名 key）
        // 生产入口（mainnet）必须显式禁用：禁止把 secrets 落地到本地文件。
        if (allowLocalFileStore)
            builder.AddJsonFile(AevatarPaths.SecretsJson, optional: true, reloadOnChange: false);

        // mcp.json — MCP 服务器配置（Cursor 兼容格式）
        builder.AddJsonFile(AevatarPaths.MCPJson, optional: true, reloadOnChange: true);

        // connectors.json — 命名连接器配置（MCP/HTTP/CLI）
        builder.AddJsonFile(AevatarPaths.ConnectorsJson, optional: true, reloadOnChange: true);

        // 环境变量（最高优先级，AEVATAR_ 前缀）
        builder.AddEnvironmentVariables("AEVATAR_");

        return builder;
    }
}
