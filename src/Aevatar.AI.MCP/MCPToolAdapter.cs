// ─────────────────────────────────────────────────────────────
// MCPToolAdapter — 将 MCP Tool 适配为 IAgentTool
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.MCP;

/// <summary>
/// 将 MCP Server 提供的 Tool 适配为 Aevatar 的 IAgentTool。
/// </summary>
public sealed class MCPToolAdapter : IAgentTool
{
    private readonly McpClient _client;
    private readonly string _serverName;
    private readonly ILogger? _logger;

    /// <summary>工具名称。</summary>
    public string Name { get; }

    /// <summary>工具描述。</summary>
    public string Description { get; }

    /// <summary>参数 JSON Schema。</summary>
    public string ParametersSchema { get; }

    public MCPToolAdapter(
        string name, string description, string parametersSchema,
        McpClient client, string serverName, ILogger? logger = null)
    {
        Name = name;
        Description = description;
        ParametersSchema = parametersSchema;
        _client = client;
        _serverName = serverName;
        _logger = logger;
    }

    /// <summary>通过 MCP 协议执行工具。</summary>
    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        _logger?.LogDebug("MCP Tool {Name} 执行: server={Server}", Name, _serverName);

        try
        {
            // 解析参数
            var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)
                       ?? [];

            var result = await _client.CallToolAsync(Name, args, cancellationToken: ct);

            // 提取文本结果
            return result.Content?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "MCP Tool {Name} 执行失败", Name);
            return System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
