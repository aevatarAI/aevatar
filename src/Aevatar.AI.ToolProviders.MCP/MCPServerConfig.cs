// ─────────────────────────────────────────────────────────────
// MCPServerConfig — MCP 服务器配置模型
// ─────────────────────────────────────────────────────────────

namespace Aevatar.AI.ToolProviders.MCP;

/// <summary>MCP 服务器配置。</summary>
public sealed class MCPServerConfig
{
    /// <summary>服务器名称（用于引用）。</summary>
    public required string Name { get; init; }

    /// <summary>启动命令（如 "npx" 或 "/path/to/server"）。</summary>
    public string? Command { get; init; }

    /// <summary>HTTP/SSE endpoint URL。</summary>
    public string? Url { get; init; }

    /// <summary>HTTP 请求头。</summary>
    public Dictionary<string, string> Headers { get; init; } = [];

    /// <summary>OAuth 认证配置。</summary>
    public MCPAuthConfig? Auth { get; init; }

    /// <summary>是否为 HTTP 传输。</summary>
    public bool IsHttp => !string.IsNullOrWhiteSpace(Url);

    /// <summary>命令参数。</summary>
    public string[] Arguments { get; init; } = [];

    /// <summary>环境变量。</summary>
    public Dictionary<string, string> Environment { get; init; } = [];
}

/// <summary>MCP OAuth 认证配置。</summary>
public sealed class MCPAuthConfig
{
    public string Type { get; init; } = "client_credentials";
    public required string TokenUrl { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public string? Scope { get; init; }
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

    /// <summary>添加一个 HTTP/SSE MCP 服务器。</summary>
    public MCPToolsOptions AddHttpServer(
        string name, string url,
        Dictionary<string, string>? headers = null,
        MCPAuthConfig? auth = null)
    {
        Servers.Add(new MCPServerConfig
        {
            Name = name,
            Url = url,
            Headers = headers ?? [],
            Auth = auth,
        });
        return this;
    }
}
