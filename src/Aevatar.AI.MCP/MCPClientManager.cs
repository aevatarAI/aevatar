// ─────────────────────────────────────────────────────────────
// MCPClientManager — 管理 MCP Server 连接
// 自动连接、发现工具、适配为 IAgentTool
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.MCP;

/// <summary>
/// 管理 MCP Server 连接。连接后自动发现工具并适配为 IAgentTool。
/// </summary>
public sealed class MCPClientManager : IAsyncDisposable
{
    private readonly List<McpClient> _clients = [];
    private readonly ILogger _logger;

    public MCPClientManager(ILogger? logger = null) =>
        _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// 连接到 MCP Server 并发现其工具。返回适配后的 IAgentTool 列表。
    /// </summary>
    public async Task<IReadOnlyList<IAgentTool>> ConnectAndDiscoverAsync(
        MCPServerConfig config, CancellationToken ct = default)
    {
        _logger.LogInformation("连接 MCP Server: {Name} ({Command})", config.Name, config.Command);

        // 创建 stdio transport
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = config.Command,
            Arguments = [..config.Arguments],
        });

        var options = new McpClientOptions
        {
            InitializationTimeout = TimeSpan.FromSeconds(30),
        };

        var client = await McpClient.CreateAsync(transport, options, cancellationToken: ct);
        _clients.Add(client);

        // 发现工具
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        var adapted = new List<IAgentTool>();

        foreach (var tool in tools)
        {
            var schema = tool.JsonSchema.GetRawText();
            adapted.Add(new MCPToolAdapter(
                tool.Name,
                tool.Description ?? "",
                schema,
                client,
                config.Name,
                _logger));
        }

        _logger.LogInformation("MCP Server {Name}: 发现 {Count} 个工具", config.Name, adapted.Count);
        return adapted;
    }

    /// <summary>释放所有 MCP 连接。</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();
        _clients.Clear();
    }
}
