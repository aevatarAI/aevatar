// ─────────────────────────────────────────────────────────────
// MCPClientManager — 管理 MCP Server 连接
// 自动连接、发现工具、适配为 IAgentTool
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.ToolProviders;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.MCP;

/// <summary>
/// 管理 MCP Server 连接。连接后自动发现工具并适配为 IAgentTool。
/// </summary>
public sealed class MCPClientManager : IAsyncDisposable
{
    private readonly object _clientsLock = new();
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
        _logger.LogInformation("连接 MCP Server: {Name} (IsHttp={IsHttp})", config.Name, config.IsHttp);

        IClientTransport transport;

        if (config.IsHttp)
        {
            HttpClient httpClient;

            if (config.AuthHandler is { } authHandler)
            {
                httpClient = new HttpClient(authHandler);
            }
            else if (config.Auth is { } auth)
            {
                // Use a delegating handler that auto-refreshes the OAuth token
                var handler = new OAuthTokenHandler(auth, _logger);
                httpClient = new HttpClient(handler);
            }
            else
            {
                httpClient = new HttpClient();
            }

            try
            {
                // Apply static headers
                foreach (var (key, value) in config.Headers)
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);

                transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Name = config.Name,
                    Endpoint = new Uri(config.Url!),
                }, httpClient);
            }
            catch
            {
                httpClient.Dispose();
                throw;
            }
        }
        else
        {
            transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = config.Name,
                Command = config.Command!,
                Arguments = [..config.Arguments],
                EnvironmentVariables = config.Environment.Count == 0
                    ? null
                    : config.Environment.ToDictionary(static kv => kv.Key, static kv => (string?)kv.Value),
            });
        }

        var options = new McpClientOptions
        {
            InitializationTimeout = TimeSpan.FromSeconds(30),
        };

        var client = await McpClient.CreateAsync(transport, options, cancellationToken: ct);
        lock (_clientsLock) { _clients.Add(client); }

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
        List<McpClient> snapshot;
        lock (_clientsLock)
        {
            snapshot = [.._clients];
            _clients.Clear();
        }
        foreach (var client in snapshot)
            await client.DisposeAsync();
    }
}
