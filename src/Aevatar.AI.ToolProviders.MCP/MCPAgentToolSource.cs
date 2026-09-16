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
    private readonly TimeProvider _timeProvider;
    private volatile Lazy<Task<CachedToolSnapshot>>? _cachedTools;

    public MCPAgentToolSource(
        MCPToolsOptions options,
        IMCPToolDiscoveryPort clientManager,
        ILogger<MCPAgentToolSource>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _clientManager = clientManager;
        _logger = logger ?? NullLogger<MCPAgentToolSource>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var current = _cachedTools;
            if (TryGetReusableTask(current, out var cached))
                return (await cached).Tools;

            // Refactor (iter88/cluster-088):
            // Old: cache miss started MCP discovery before CompareExchange, so loser calls still
            // created external MCP clients.
            // New: cache the non-started Lazy<Task<T>> first; only the winning Lazy evaluates.
            var candidate = new Lazy<Task<CachedToolSnapshot>>(
                () => DiscoverAllAsync(_options, _clientManager, _logger, _timeProvider, ct),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var winner = Interlocked.CompareExchange(ref _cachedTools, candidate, current);
            if (ReferenceEquals(winner, current))
                return (await candidate.Value).Tools;
        }
    }

    private bool TryGetReusableTask(
        Lazy<Task<CachedToolSnapshot>>? current,
        out Task<CachedToolSnapshot> task)
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
        if (!existing.IsCompletedSuccessfully)
        {
            if (!existing.IsCompleted)
            {
                task = existing;
                return true;
            }

            return false;
        }

        var snapshot = existing.Result;
        if (snapshot.TimeToLive.HasValue &&
            (snapshot.TimeToLive <= TimeSpan.Zero ||
             _timeProvider.GetElapsedTime(snapshot.DiscoveredAtTimestamp) >= snapshot.TimeToLive))
            return false;

        task = existing;
        return true;
    }

    private static async Task<CachedToolSnapshot> DiscoverAllAsync(
        MCPToolsOptions options,
        IMCPToolDiscoveryPort clientManager,
        ILogger logger,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        if (options.Servers.Count == 0)
            return new CachedToolSnapshot([], timeProvider.GetTimestamp(), null);

        var tools = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        TimeSpan? timeToLive = null;
        foreach (var server in options.Servers)
        {
            try
            {
                var discovered = await clientManager.ConnectAndDiscoverAsync(server, ct);
                foreach (var tool in discovered.Tools)
                    tools[tool.Name] = tool;
                timeToLive = Shortest(timeToLive, discovered.TimeToLive);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MCP tool discovery failed for server {ServerName}", server.Name);
                timeToLive = TimeSpan.Zero;
            }
        }

        return new CachedToolSnapshot(tools.Values.ToList(), timeProvider.GetTimestamp(), timeToLive);
    }

    private static TimeSpan? Shortest(TimeSpan? left, TimeSpan? right) =>
        !left.HasValue ? right : !right.HasValue || left.Value <= right.Value ? left : right;

    private sealed record CachedToolSnapshot(
        IReadOnlyList<IAgentTool> Tools,
        long DiscoveredAtTimestamp,
        TimeSpan? TimeToLive);
}
