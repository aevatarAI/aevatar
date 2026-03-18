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
    private readonly MCPToolsOptions _options;
    private readonly MCPClientManager _clientManager;
    private readonly ILogger _logger;
    private volatile Task<IReadOnlyList<IAgentTool>>? _cachedTools;

    public MCPAgentToolSource(
        MCPToolsOptions options,
        MCPClientManager clientManager,
        ILogger<MCPAgentToolSource>? logger = null)
    {
        _options = options;
        _clientManager = clientManager;
        _logger = logger ?? NullLogger<MCPAgentToolSource>.Instance;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        var current = _cachedTools;
        if (current is { IsCompletedSuccessfully: true }) return current;
        var task = DiscoverAllAsync(_options, _clientManager, _logger, ct);
        var winner = Interlocked.CompareExchange(ref _cachedTools, task, current);
        return ReferenceEquals(winner, current) ? task : winner!;
    }

    private static async Task<IReadOnlyList<IAgentTool>> DiscoverAllAsync(
        MCPToolsOptions options, MCPClientManager clientManager, ILogger logger, CancellationToken ct)
    {
        if (options.Servers.Count == 0)
            return [];

        var tools = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in options.Servers)
        {
            try
            {
                var discovered = await clientManager.ConnectAndDiscoverAsync(server, ct);
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
