// ─────────────────────────────────────────────────────────────
// AevatarMcpConfig — mcp.json 读取
//
// 解析 Cursor 兼容的 MCP 服务器配置格式：
// { "mcpServers": { "name": { "command": "...", "args": [...] } } }
// ─────────────────────────────────────────────────────────────

using System.Text.Json;

namespace Aevatar.Config;

/// <summary>
/// MCP 服务器配置条目。
/// </summary>
public sealed class McpServerEntry
{
    /// <summary>服务器名称。</summary>
    public required string Name { get; init; }

    /// <summary>启动命令。</summary>
    public string Command { get; init; } = "";

    /// <summary>命令参数。</summary>
    public string[] Args { get; init; } = [];

    /// <summary>环境变量。</summary>
    public Dictionary<string, string> Env { get; init; } = [];

    /// <summary>超时（毫秒）。</summary>
    public int TimeoutMs { get; init; } = 60000;
}

/// <summary>
/// 从 ~/.aevatar/mcp.json 读取 MCP 服务器配置。
/// </summary>
public static class AevatarMcpConfig
{
    /// <summary>
    /// 读取所有 MCP 服务器配置。
    /// </summary>
    public static IReadOnlyList<McpServerEntry> LoadServers(string? filePath = null)
    {
        var path = filePath ?? AevatarPaths.McpJson;
        if (!File.Exists(path)) return [];

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers))
                return [];

            var result = new List<McpServerEntry>();
            foreach (var prop in servers.EnumerateObject())
            {
                var entry = new McpServerEntry
                {
                    Name = prop.Name,
                    Command = prop.Value.TryGetProperty("command", out var cmd) ? cmd.GetString() ?? "" : "",
                    Args = prop.Value.TryGetProperty("args", out var args)
                        ? args.EnumerateArray().Select(a => a.GetString() ?? "").ToArray()
                        : [],
                    TimeoutMs = prop.Value.TryGetProperty("timeoutMs", out var tm) ? tm.GetInt32() : 60000,
                };

                if (prop.Value.TryGetProperty("env", out var env))
                {
                    foreach (var e in env.EnumerateObject())
                        entry.Env[e.Name] = e.Value.GetString() ?? "";
                }

                result.Add(entry);
            }
            return result;
        }
        catch { return []; }
    }
}
