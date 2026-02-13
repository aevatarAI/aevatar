// ─────────────────────────────────────────────────────────────
// ServiceCollectionExtensions — MCP Tools DI 注册
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.MCP;

/// <summary>MCP Tools 的 DI 注册扩展。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 MCP Tool 集成。配置 MCP 服务器后，工具自动注册到 Agent。
    /// </summary>
    /// <example>
    /// services.AddMCPTools(o => o
    ///     .AddServer("filesystem", "npx", "-y", "@modelcontextprotocol/server-filesystem", "/tmp")
    ///     .AddServer("memory", "npx", "-y", "@modelcontextprotocol/server-memory"));
    /// </example>
    public static IServiceCollection AddMCPTools(
        this IServiceCollection services,
        Action<MCPToolsOptions> configure)
    {
        var options = new MCPToolsOptions();
        configure(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<MCPClientManager>();
        return services;
    }
}
