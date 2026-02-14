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
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<IAgentTool>? _cachedTools;

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
    public async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (_cachedTools != null) return _cachedTools;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cachedTools != null) return _cachedTools;
            if (_options.Servers.Count == 0)
            {
                _cachedTools = [];
                return _cachedTools;
            }

            var tools = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
            foreach (var server in _options.Servers)
            {
                try
                {
                    var discovered = await _clientManager.ConnectAndDiscoverAsync(server, ct);
                    foreach (var tool in discovered)
                        tools[tool.Name] = tool;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MCP tool discovery failed for server {ServerName}", server.Name);
                }
            }

            _cachedTools = tools.Values.ToList();
            return _cachedTools;
        }
        finally
        {
            _lock.Release();
        }
    }
}
