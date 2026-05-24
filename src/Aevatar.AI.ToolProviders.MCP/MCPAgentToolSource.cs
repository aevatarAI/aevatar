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
    private readonly IMCPToolDiscoveryPort _clientManager;
    private readonly ILogger _logger;
    private volatile Lazy<Task<IReadOnlyList<IAgentTool>>>? _cachedTools;

    public MCPAgentToolSource(
        MCPToolsOptions options,
        IMCPToolDiscoveryPort clientManager,
        ILogger<MCPAgentToolSource>? logger = null)
    {
        _options = options;
        _clientManager = clientManager;
        _logger = logger ?? NullLogger<MCPAgentToolSource>.Instance;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var current = _cachedTools;
            if (TryGetReusableTask(current, out var cached))
                return cached;

            // Refactor (iter88/cluster-088):
            // Old: cache miss started MCP discovery before CompareExchange, so loser calls still
            // created external MCP clients.
            // New: cache the non-started Lazy<Task<T>> first; only the winning Lazy evaluates.
            var candidate = new Lazy<Task<IReadOnlyList<IAgentTool>>>(
                () => DiscoverAllAsync(_options, _clientManager, _logger, ct),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var winner = Interlocked.CompareExchange(ref _cachedTools, candidate, current);
            if (ReferenceEquals(winner, current))
                return candidate.Value;
        }
    }

    private static bool TryGetReusableTask(
        Lazy<Task<IReadOnlyList<IAgentTool>>>? current,
        out Task<IReadOnlyList<IAgentTool>> task)
    {
        task = null!;
        if (current == null)
            return false;

        if (!current.IsValueCreated)
        {
            task = current.Value;
            return true;
        }

        var existing = current.Value;
        if (existing.IsFaulted || existing.IsCanceled)
            return false;

        task = existing;
        return true;
    }

    private static async Task<IReadOnlyList<IAgentTool>> DiscoverAllAsync(
        MCPToolsOptions options, IMCPToolDiscoveryPort clientManager, ILogger logger, CancellationToken ct)
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
