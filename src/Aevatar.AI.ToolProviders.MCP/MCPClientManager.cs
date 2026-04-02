// ─────────────────────────────────────────────────────────────
// MCPClientManager — 管理 MCP Server 连接
// 自动连接、发现工具、适配为 IAgentTool
// ─────────────────────────────────────────────────────────────

using System.Collections.Immutable;
using Aevatar.AI.Abstractions.ToolProviders;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.MCP;

/// <summary>
/// 管理 MCP Server 连接。连接后自动发现工具并适配为 IAgentTool。
/// Clients are tracked via an immutable list for thread-safe append;
/// disposal iterates a snapshot and is intended to be called once at shutdown.
/// </summary>
public sealed class MCPClientManager : IAsyncDisposable
{
    private ImmutableList<McpClient> _clients = ImmutableList<McpClient>.Empty;
    private readonly ILogger _logger;

    public MCPClientManager(ILogger? logger = null) =>
        _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// 连接到 MCP Server 并发现其工具。返回适配后的 IAgentTool 列表。
    /// </summary>
    public async Task<IReadOnlyList<IAgentTool>> ConnectAndDiscoverAsync(
        MCPServerConfig config, CancellationToken ct = default)
    {
        var endpoint = !string.IsNullOrWhiteSpace(config.Url) ? config.Url : config.Command;
        _logger.LogInformation("连接 MCP Server: {Name} ({Endpoint})", config.Name, endpoint);

        var transport = CreateTransport(config);

        var options = new McpClientOptions
        {
            InitializationTimeout = TimeSpan.FromSeconds(30),
        };

        var client = await McpClient.CreateAsync(transport, options, cancellationToken: ct);
        ImmutableInterlocked.Update(ref _clients, list => list.Add(client));

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

    private static IClientTransport CreateTransport(MCPServerConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Url))
        {
            var options = new HttpClientTransportOptions
            {
                Name = config.Name,
                Endpoint = new Uri(config.Url, UriKind.Absolute),
                TransportMode = HttpTransportMode.AutoDetect,
                ConnectionTimeout = TimeSpan.FromSeconds(30),
                AdditionalHeaders = config.AdditionalHeaders.Count == 0
                    ? null
                    : new Dictionary<string, string>(config.AdditionalHeaders, StringComparer.OrdinalIgnoreCase),
            };

            return config.HttpClient != null
                ? new HttpClientTransport(options, config.HttpClient, NullLoggerFactory.Instance, true)
                : new HttpClientTransport(options, NullLoggerFactory.Instance);
        }

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = config.Name,
            Command = config.Command,
            Arguments = [..config.Arguments],
            EnvironmentVariables = config.Environment.Count == 0
                ? null
                : config.Environment.ToDictionary(static kv => kv.Key, static kv => (string?)kv.Value),
        });
    }

    /// <summary>释放所有 MCP 连接。</summary>
    public async ValueTask DisposeAsync()
    {
        var snapshot = Interlocked.Exchange(ref _clients, ImmutableList<McpClient>.Empty);
        foreach (var client in snapshot)
            await client.DisposeAsync();
    }
}
