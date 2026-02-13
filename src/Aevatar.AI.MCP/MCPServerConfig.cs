// ─────────────────────────────────────────────────────────────
// MCPServerConfig — MCP 服务器配置模型
// ─────────────────────────────────────────────────────────────

namespace Aevatar.AI.MCP;

/// <summary>MCP 服务器配置。</summary>
public sealed class MCPServerConfig
{
    /// <summary>服务器名称（用于引用）。</summary>
    public required string Name { get; init; }

    /// <summary>启动命令（如 "npx" 或 "/path/to/server"）。</summary>
    public required string Command { get; init; }

    /// <summary>命令参数。</summary>
    public string[] Arguments { get; init; } = [];

    /// <summary>环境变量。</summary>
    public Dictionary<string, string> Environment { get; init; } = [];
}

/// <summary>MCP Tools 选项。</summary>
public sealed class MCPToolsOptions
{
    /// <summary>MCP 服务器列表。</summary>
    public List<MCPServerConfig> Servers { get; } = [];

    /// <summary>添加一个 MCP 服务器。</summary>
    public MCPToolsOptions AddServer(string name, string command, params string[] arguments)
    {
        Servers.Add(new MCPServerConfig { Name = name, Command = command, Arguments = arguments });
        return this;
    }
}
