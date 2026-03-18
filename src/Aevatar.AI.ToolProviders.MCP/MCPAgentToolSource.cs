using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.MCP;

/// <summary>
/// MCP 工具来源。按配置连接 MCP 服务器并发现可用工具。
/// 结果会缓存，避免重复建连和重复发现。
/// </summary>
public sealed class MCPAgentToolSource : IAgentToolSource
{
    private readonly Lazy<Task<IReadOnlyList<IAgentTool>>> _cachedTools;

    public MCPAgentToolSource(
        MCPToolsOptions options,
        MCPClientManager clientManager,
        ILogger<MCPAgentToolSource>? logger = null)
    {
        var log = logger ?? NullLogger<MCPAgentToolSource>.Instance;
        _cachedTools = new Lazy<Task<IReadOnlyList<IAgentTool>>>(() => DiscoverAllAsync(options, clientManager, log));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        => _cachedTools.Value;

    private static async Task<IReadOnlyList<IAgentTool>> DiscoverAllAsync(
        MCPToolsOptions options, MCPClientManager clientManager, ILogger logger)
    {
        if (options.Servers.Count == 0)
            return [];

        var tools = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in options.Servers)
        {
            try
            {
                var discovered = await clientManager.ConnectAndDiscoverAsync(server);
                foreach (var tool in discovered)
                    tools[tool.Name] = tool;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MCP tool discovery failed for server {ServerName}", server.Name);
            }
        }

        return tools.Values.ToList();
    }
}
