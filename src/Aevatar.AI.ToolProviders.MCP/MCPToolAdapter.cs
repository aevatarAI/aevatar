// ─────────────────────────────────────────────────────────────
// MCPToolAdapter — 将 MCP Tool 适配为 IAgentTool
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.MCP;

/// <summary>
/// 将 MCP Server 提供的 Tool 适配为 Aevatar 的 IAgentTool。
/// </summary>
public sealed class MCPToolAdapter : IAgentTool
{
    private readonly McpClient _client;
    private readonly string _mcpToolName;
    private readonly string _serverName;
    private readonly ILogger? _logger;

    /// <summary>LLM 可见的工具名称（已 sanitize，仅含 [a-zA-Z0-9_-]）。</summary>
    public string Name { get; }

    /// <summary>工具描述。</summary>
    public string Description { get; }

    /// <summary>参数 JSON Schema。</summary>
    public string ParametersSchema { get; }

    public ToolPresentationDescriptor Presentation =>
        ToolPresentationDescriptors.Mcp(
            Name,
            _mcpToolName,
            Description,
            _serverName,
            _mcpToolName);

    public MCPToolAdapter(
        string name, string description, string parametersSchema,
        McpClient client, string serverName, ILogger? logger = null)
    {
        _mcpToolName = name;
        Name = SanitizeToolName(name);
        Description = description;
        ParametersSchema = parametersSchema;
        _client = client;
        _serverName = serverName;
        _logger = logger;
    }

    /// <summary>通过 MCP 协议执行工具（使用原始 MCP tool name）。</summary>
    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        (await ExecuteWithOutcomeAsync(string.Empty, Name, argumentsJson, ct).ConfigureAwait(false)).ResultJson;

    public async Task<AgentToolTerminalOutcome> ExecuteWithOutcomeAsync(
        string callId,
        string toolName,
        string argumentsJson,
        CancellationToken ct = default)
    {
        _logger?.LogDebug("MCP Tool {Name} (mcp={McpName}) 执行: server={Server}",
            Name, _mcpToolName, _serverName);

        try
        {
            var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)
                       ?? [];

            var result = await _client.CallToolAsync(_mcpToolName, args, cancellationToken: ct);
            var resultJson = result.Content?.ToString() ?? "";
            var isError = result.IsError == true;
            return new AgentToolTerminalOutcome(
                resultJson,
                CreateReceipt(
                    callId,
                    toolName,
                    resultJson,
                    isError ? AgentToolReceiptStatus.Error : AgentToolReceiptStatus.Success,
                    isError ? "tool_execution_error" : string.Empty,
                    isError ? "The MCP tool reported an execution error." : string.Empty));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "MCP Tool {Name} 执行失败", Name);
            var resultJson = System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message });
            return new AgentToolTerminalOutcome(
                resultJson,
                CreateReceipt(
                    callId,
                    toolName,
                    resultJson,
                    AgentToolReceiptStatus.Error,
                    "tool_execution_error",
                    ex.GetType().Name));
        }
    }

    private AgentToolReceipt CreateReceipt(
        string callId,
        string toolName,
        string resultJson,
        AgentToolReceiptStatus status,
        string errorCode,
        string errorMessage) =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
            Status = status,
            ResultJson = resultJson,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
        };

    /// <summary>
    /// 将 tool name sanitize 为 OpenAI 兼容格式：仅保留 [a-zA-Z0-9_-]，
    /// 其他字符替换为 '_'，连续 '_' 合并。
    /// </summary>
    internal static string SanitizeToolName(string name)
    {
        var chars = new char[name.Length];
        var len = 0;
        var lastWasUnderscore = false;

        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '-')
            {
                chars[len++] = c;
                lastWasUnderscore = false;
            }
            else if (!lastWasUnderscore)
            {
                chars[len++] = '_';
                lastWasUnderscore = true;
            }
        }

        // 去掉末尾的 '_'
        while (len > 0 && chars[len - 1] == '_') len--;

        return len > 0 ? new string(chars, 0, len) : "unnamed_tool";
    }
}
