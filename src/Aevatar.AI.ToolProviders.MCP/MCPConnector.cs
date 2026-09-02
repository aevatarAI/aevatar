using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Connectors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.MCP;

/// <summary>
/// MCP-based connector.
/// Connects to one MCP server and executes an allowlisted tool.
/// </summary>
public sealed class MCPConnector : IConnector, IAsyncDisposable
{
    private readonly IMCPToolDiscoveryPort _clientManager;
    private readonly IAgentToolExecutionPort _toolExecutionPort;
    private readonly MCPServerConfig _serverConfig;
    private readonly string? _defaultTool;
    private readonly HashSet<string> _allowedTools;
    private readonly HashSet<string> _allowedInputKeys;
    private readonly CancellationTokenSource _disposeCts = new();
    private volatile Lazy<Task<IReadOnlyDictionary<string, IAgentTool>>>? _tools;
    private readonly ILogger _logger;
    private readonly bool _ownsClientManager;
    private int _disposed;

    public MCPConnector(
        string name,
        MCPServerConfig serverConfig,
        string? defaultTool = null,
        IEnumerable<string>? allowedTools = null,
        IEnumerable<string>? allowedInputKeys = null,
        IMCPToolDiscoveryPort? clientManager = null,
        IAgentToolExecutionPort? toolExecutionPort = null,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required", nameof(name));
        Name = name;
        _serverConfig = serverConfig ?? throw new ArgumentNullException(nameof(serverConfig));
        _defaultTool = defaultTool;
        _allowedTools = new HashSet<string>(allowedTools ?? [], StringComparer.OrdinalIgnoreCase);
        _allowedInputKeys = new HashSet<string>(allowedInputKeys ?? [], StringComparer.OrdinalIgnoreCase);
        _clientManager = clientManager ?? new MCPClientManager(logger);
        _toolExecutionPort = toolExecutionPort
            ?? throw new ArgumentNullException(nameof(toolExecutionPort));
        _ownsClientManager = clientManager == null;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Type => "mcp";

    /// <inheritdoc />
    public async Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sw = Stopwatch.StartNew();
        try
        {
            if (!TryCreateRequestIdentity(request, out var requestIdentity))
            {
                return new ConnectorResponse
                {
                    Success = false,
                    Error = "mcp connector requires stable run, step, idempotency, and issued-time identities",
                };
            }

            var tools = await GetOrConnectAsync(ct);

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

            if (!tools.TryGetValue(toolName, out var tool))
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

            var outcome = await _toolExecutionPort.ExecuteAsync(
                new AgentToolExecutionRequest(
                    tool,
                    request.Payload ?? string.Empty,
                    AgentToolExecutionContext.Empty with
                    {
                        Request = requestIdentity,
                        ExecutionOwner = AgentToolExecutionOwners.Connector(Name),
                    },
                    AgentToolApprovalContinuationMode.None,
                    null),
                ct).ConfigureAwait(false);
            if (outcome.Kind is not (AgentToolExecutionOutcomeKind.Executed or
                    AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete) ||
                outcome.Receipt.Status != AgentToolReceiptStatus.Success)
            {
                sw.Stop();
                return new ConnectorResponse
                {
                    Success = false,
                    Error = ResolveFailureMessage(outcome),
                    TerminalInvoked = outcome.TerminalInvoked,
                    Retryable = outcome.Retryable,
                    Metadata = new Dictionary<string, string>
                    {
                        ["connector.mcp.server"] = _serverConfig.Name,
                        ["connector.mcp.tool"] = toolName,
                        ["connector.mcp.duration_ms"] = sw.Elapsed.TotalMilliseconds.ToString("F2"),
                    },
                };
            }

            var result = outcome.ResultJson;
            sw.Stop();
            return new ConnectorResponse
            {
                Success = true,
                Output = result,
                TerminalInvoked = outcome.TerminalInvoked,
                Retryable = outcome.Retryable,
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
                TerminalInvoked = false,
                Retryable = true,
                Metadata = new Dictionary<string, string>
                {
                    ["connector.mcp.server"] = _serverConfig.Name,
                    ["connector.mcp.duration_ms"] = sw.Elapsed.TotalMilliseconds.ToString("F2"),
                },
            };
        }
    }

    private static string ResolveFailureMessage(AgentToolExecutionOutcome outcome)
    {
        if (!string.IsNullOrWhiteSpace(outcome.SafeMessage))
            return outcome.SafeMessage;
        if (!string.IsNullOrWhiteSpace(outcome.Receipt.ErrorMessage))
            return outcome.Receipt.ErrorMessage;
        if (!string.IsNullOrWhiteSpace(outcome.FailureCode))
            return outcome.FailureCode;
        if (!string.IsNullOrWhiteSpace(outcome.Receipt.ErrorCode))
            return outcome.Receipt.ErrorCode;

        return "The MCP tool did not report a verified success.";
    }

    private string ResolveToolName(ConnectorRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Operation))
            return request.Operation;
        if (request.Parameters.TryGetValue("tool", out var toolFromParams) && !string.IsNullOrWhiteSpace(toolFromParams))
            return toolFromParams;
        return _defaultTool ?? "";
    }

    private static bool TryCreateRequestIdentity(
        ConnectorRequest request,
        out AgentToolRequestIdentity identity)
    {
        var runId = Normalize(request.RunId);
        var stepId = Normalize(request.StepId);
        var idempotencyKey = Normalize(request.IdempotencyKey);
        if (runId is null || stepId is null || idempotencyKey is null || request.IssuedAtUnixMs <= 0)
        {
            identity = AgentToolRequestIdentity.Empty;
            return false;
        }

        identity = new AgentToolRequestIdentity(
            "workflow-connector:v1:" + HashLengthPrefixed(runId, stepId),
            idempotencyKey,
            idempotencyKey,
            request.IssuedAtUnixMs);
        return true;
    }

    private static string HashLengthPrefixed(params string[] values)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[sizeof(uint)];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            stream.Write(length);
            stream.Write(bytes);
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Task<IReadOnlyDictionary<string, IAgentTool>> GetOrConnectAsync(CancellationToken ct)
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var current = _tools;
            if (TryGetReusableTask(current, out var cached))
                return cached;

            // Refactor (iter88/cluster-088):
            // Old: cache miss started ConnectAndIndexToolsAsync before CompareExchange, so losing callers
            // could still open external MCP clients.
            // New: publish a non-started Lazy<Task<T>> first; ExecutionAndPublication lets only the
            // winning Lazy start discovery, while losing Lazy instances are never evaluated.
            var candidate = new Lazy<Task<IReadOnlyDictionary<string, IAgentTool>>>(
                () => ConnectAndIndexToolsAsync(ct, _disposeCts.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var winner = Interlocked.CompareExchange(ref _tools, candidate, current);
            if (ReferenceEquals(winner, current))
                return candidate.Value;
        }
    }

    private static bool TryGetReusableTask(
        Lazy<Task<IReadOnlyDictionary<string, IAgentTool>>>? current,
        out Task<IReadOnlyDictionary<string, IAgentTool>> task)
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

    private async Task<IReadOnlyDictionary<string, IAgentTool>> ConnectAndIndexToolsAsync(
        CancellationToken requestCt,
        CancellationToken disposeCt)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(requestCt, disposeCt);
        var ct = linkedCts.Token;
        var discovered = await _clientManager.ConnectAndDiscoverAsync(_serverConfig, ct);
        return discovered.ToFrozenDictionary(t => t.Name, t => t, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _disposeCts.CancelAsync();
        var toolsTask = TryGetCreatedToolsTask(_tools);
        if (toolsTask != null)
        {
            try
            {
                await toolsTask;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP discovery ended with an error while connector {ConnectorName} was shutting down", Name);
            }
        }

        if (_ownsClientManager && _clientManager is IAsyncDisposable disposableClientManager)
            await disposableClientManager.DisposeAsync();

        // Refactor (iter87/cluster-087):
        //   Old pattern: remote MCP HttpClient was transferred into connector config but only connected sessions were disposed.
        //   New principle: connector lifecycle releases owned transport resources even when disposed before first connect.
        if (_serverConfig.OwnsHttpClient)
            _serverConfig.HttpClient?.Dispose();

        _disposeCts.Dispose();
    }

    private static Task<IReadOnlyDictionary<string, IAgentTool>>? TryGetCreatedToolsTask(
        Lazy<Task<IReadOnlyDictionary<string, IAgentTool>>>? current)
    {
        return current is { IsValueCreated: true } ? current.Value : null;
    }

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
