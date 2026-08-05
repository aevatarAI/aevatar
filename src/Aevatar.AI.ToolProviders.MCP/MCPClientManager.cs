// ─────────────────────────────────────────────────────────────
// MCPClientManager — 管理 MCP Server 连接
// 自动连接、发现工具、适配为 IAgentTool
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Aevatar.AI.Abstractions.ToolProviders;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.MCP;

/// <summary>
/// 管理 MCP Server 连接。连接后自动发现工具并适配为 IAgentTool。
/// One runtime client is reused per configured server so protocol-driven catalog refreshes do not
/// restart stdio processes or recreate remote sessions.
/// </summary>
public sealed class MCPClientManager : IMCPToolDiscoveryPort, IAsyncDisposable
{
    private ConcurrentDictionary<MCPServerConfig, Lazy<Task<ManagedClient>>> _clients = new();
    private readonly ILogger _logger;
    private int _disposed;

    public MCPClientManager(ILogger? logger = null) =>
        _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// 连接到 MCP Server 并发现其工具。返回适配后的 IAgentTool 列表。
    /// </summary>
    public async Task<MCPToolDiscoveryResult> ConnectAndDiscoverAsync(
        MCPServerConfig config, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var endpoint = !string.IsNullOrWhiteSpace(config.Url) ? config.Url : config.Command;
        _logger.LogInformation("连接 MCP Server: {Name} ({Endpoint})", config.Name, endpoint);

        var managedClient = await GetOrCreateClientAsync(config, ct);
        await managedClient.DiscoveryGate.WaitAsync(ct);
        try
        {
            return await DiscoverToolsAsync(config, managedClient.Client, ct);
        }
        finally
        {
            managedClient.DiscoveryGate.Release();
        }
    }

    private async Task<ManagedClient> GetOrCreateClientAsync(
        MCPServerConfig config,
        CancellationToken ct)
    {
        var candidate = _clients.GetOrAdd(
            config,
            static (key, state) => new Lazy<Task<ManagedClient>>(
                () => state.Manager.CreateClientAsync(key, state.CancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication),
            (Manager: this, CancellationToken: ct));

        try
        {
            var managedClient = await candidate.Value;
            if (Volatile.Read(ref _disposed) == 0)
                return managedClient;

            ((ICollection<KeyValuePair<MCPServerConfig, Lazy<Task<ManagedClient>>>>)_clients)
                .Remove(new KeyValuePair<MCPServerConfig, Lazy<Task<ManagedClient>>>(config, candidate));
            await managedClient.DisposeAsync();
            throw new ObjectDisposedException(nameof(MCPClientManager));
        }
        catch
        {
            ((ICollection<KeyValuePair<MCPServerConfig, Lazy<Task<ManagedClient>>>>)_clients)
                .Remove(new KeyValuePair<MCPServerConfig, Lazy<Task<ManagedClient>>>(config, candidate));
            throw;
        }
    }

    private async Task<ManagedClient> CreateClientAsync(MCPServerConfig config, CancellationToken ct)
    {
        var transport = CreateTransport(config);

        var options = new McpClientOptions
        {
            // A null protocol version prefers MCP 2026-07-28 discovery and lets the SDK
            // fall back to the legacy initialize handshake for down-level servers.
            ProtocolVersion = null,
            InitializationTimeout = config.InitializationTimeout,
        };

        var client = await McpClient.CreateAsync(transport, options, cancellationToken: ct);

        _logger.LogInformation(
            "MCP Server {Name}: 协商协议版本 {ProtocolVersion}",
            config.Name,
            client.NegotiatedProtocolVersion);

        return new ManagedClient(client);
    }

    private async Task<MCPToolDiscoveryResult> DiscoverToolsAsync(
        MCPServerConfig config,
        McpClient client,
        CancellationToken ct)
    {
        var protocolTools = new List<Tool>();
        var request = new ListToolsRequestParams();
        TimeSpan? timeToLive = null;
        do
        {
            var page = await client.ListToolsAsync(request, ct);
            protocolTools.AddRange(page.Tools);
            var pageTimeToLive = page.TimeToLive.GetValueOrDefault();
            if (pageTimeToLive < TimeSpan.Zero)
                pageTimeToLive = TimeSpan.Zero;
            timeToLive = timeToLive.HasValue && timeToLive.Value <= pageTimeToLive
                ? timeToLive
                : pageTimeToLive;

            _logger.LogDebug(
                "MCP Server {Name}: tools/list cache TTL {TimeToLive}, scope {CacheScope}",
                config.Name,
                pageTimeToLive,
                page.CacheScope ?? CacheScope.Public);
            request.Cursor = page.NextCursor;
        }
        while (request.Cursor is not null);

        var adapted = new List<IAgentTool>();
        client.ClearKnownTools();
        foreach (var tool in protocolTools)
        {
            try
            {
                // Register accepted definitions so the SDK can map x-mcp-header parameters
                // onto Streamable HTTP requests without exposing them in the JSON body.
                client.AddKnownTools([tool]);
                adapted.Add(new MCPToolAdapter(
                    tool.Name,
                    tool.Description ?? "",
                    tool.InputSchema.GetRawText(),
                    client,
                    config.Name,
                    _logger));
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(
                    ex,
                    "MCP Server {Name}: rejected invalid tool definition {ToolName}",
                    config.Name,
                    tool.Name);
            }
        }

        _logger.LogInformation("MCP Server {Name}: 发现 {Count} 个工具", config.Name, adapted.Count);
        return new MCPToolDiscoveryResult(adapted, timeToLive);
    }

    private static IClientTransport CreateTransport(MCPServerConfig config)
    {
        var hasUrl = !string.IsNullOrWhiteSpace(config.Url);
        var hasCommand = !string.IsNullOrWhiteSpace(config.Command);
        if (hasUrl == hasCommand)
            throw new ArgumentException("Exactly one of MCP command or url is required.", nameof(config));

        if (hasUrl)
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var snapshot = Interlocked.Exchange(
            ref _clients,
            new ConcurrentDictionary<MCPServerConfig, Lazy<Task<ManagedClient>>>());
        foreach (var lazyClient in snapshot.Values)
        {
            if (!lazyClient.IsValueCreated)
                continue;

            try
            {
                await (await lazyClient.Value).DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP client disposal failed");
            }
        }
    }

    private sealed class ManagedClient(McpClient client) : IAsyncDisposable
    {
        private int _disposed;

        public McpClient Client { get; } = client;
        public SemaphoreSlim DiscoveryGate { get; } = new(1, 1);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await Client.DisposeAsync();
            DiscoveryGate.Dispose();
        }
    }
}
