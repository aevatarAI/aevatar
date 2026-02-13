using System.Diagnostics;
using System.Text.Json;
using Aevatar.AI.Tools;
using Aevatar.Connectors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.MCP;

/// <summary>
/// MCP-based connector.
/// Connects to one MCP server and executes an allowlisted tool.
/// </summary>
public sealed class MCPConnector : IConnector, IAsyncDisposable
{
    private readonly MCPClientManager _clientManager;
    private readonly MCPServerConfig _serverConfig;
    private readonly string? _defaultTool;
    private readonly HashSet<string> _allowedTools;
    private readonly HashSet<string> _allowedInputKeys;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly Dictionary<string, IAgentTool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;
    private volatile bool _initialized;

    public MCPConnector(
        string name,
        MCPServerConfig serverConfig,
        string? defaultTool = null,
        IEnumerable<string>? allowedTools = null,
        IEnumerable<string>? allowedInputKeys = null,
        MCPClientManager? clientManager = null,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required", nameof(name));
        Name = name;
        _serverConfig = serverConfig ?? throw new ArgumentNullException(nameof(serverConfig));
        _defaultTool = defaultTool;
        _allowedTools = new HashSet<string>(allowedTools ?? [], StringComparer.OrdinalIgnoreCase);
        _allowedInputKeys = new HashSet<string>(allowedInputKeys ?? [], StringComparer.OrdinalIgnoreCase);
        _clientManager = clientManager ?? new MCPClientManager(logger);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Type => "mcp";

    /// <inheritdoc />
    public async Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await EnsureConnectedAsync(ct);

            var toolName = ResolveToolName(request);
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return new ConnectorResponse
                {
                    Success = false,
                    Error = "mcp connector requires operation or default tool",
                };
            }

            if (_allowedTools.Count > 0 && !_allowedTools.Contains(toolName))
            {
                return new ConnectorResponse
                {
                    Success = false,
                    Error = $"tool '{toolName}' is not allowlisted",
                };
            }

            if (!_tools.TryGetValue(toolName, out var tool))
            {
                return new ConnectorResponse
                {
                    Success = false,
                    Error = $"tool '{toolName}' was not discovered from MCP server '{_serverConfig.Name}'",
                };
            }

            if (_allowedInputKeys.Count > 0 && !TryValidatePayloadKeys(request.Payload, _allowedInputKeys, out var schemaError))
            {
                return new ConnectorResponse
                {
                    Success = false,
                    Error = schemaError,
                };
            }

            var result = await tool.ExecuteAsync(request.Payload ?? "", ct);
            sw.Stop();
            return new ConnectorResponse
            {
                Success = true,
                Output = result,
                Metadata = new Dictionary<string, string>
                {
                    ["connector.mcp.server"] = _serverConfig.Name,
                    ["connector.mcp.tool"] = toolName,
                    ["connector.mcp.duration_ms"] = sw.Elapsed.TotalMilliseconds.ToString("F2"),
                },
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "MCP connector {Name} execution failed", Name);
            return new ConnectorResponse
            {
                Success = false,
                Error = ex.Message,
                Metadata = new Dictionary<string, string>
                {
                    ["connector.mcp.server"] = _serverConfig.Name,
                    ["connector.mcp.duration_ms"] = sw.Elapsed.TotalMilliseconds.ToString("F2"),
                },
            };
        }
    }

    private string ResolveToolName(ConnectorRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Operation))
            return request.Operation;
        if (request.Parameters.TryGetValue("tool", out var toolFromParams) && !string.IsNullOrWhiteSpace(toolFromParams))
            return toolFromParams;
        return _defaultTool ?? "";
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var tools = await _clientManager.ConnectAndDiscoverAsync(_serverConfig, ct);
            _tools.Clear();
            foreach (var tool in tools)
                _tools[tool.Name] = tool;

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _clientManager.DisposeAsync();

    private static bool TryValidatePayloadKeys(string payload, HashSet<string> allowedKeys, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(payload)) return true;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "payload schema violation: expected JSON object";
                return false;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!allowedKeys.Contains(prop.Name))
                {
                    error = $"payload schema violation: key '{prop.Name}' is not allowlisted";
                    return false;
                }
            }

            return true;
        }
        catch
        {
            error = "payload schema violation: invalid JSON";
            return false;
        }
    }
}
