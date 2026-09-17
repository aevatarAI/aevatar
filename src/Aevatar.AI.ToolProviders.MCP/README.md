# Aevatar.AI.ToolProviders.MCP

`Aevatar.AI.ToolProviders.MCP` 提供 MCP（Model Context Protocol）工具接入能力，把外部 MCP 工具桥接成 Aevatar 的 `IAgentTool`。

## 职责

- 管理 MCP Server 连接生命周期
- 发现 MCP Tool 并适配为 `IAgentTool`
- 执行工具调用并返回完整 `CallToolResult` JSON
- 提供 DI 扩展 `AddMCPTools(...)`

## 协议

- 使用官方 C# SDK 2.1，默认优先 MCP `2026-07-28`
- `2026-07-28` 使用无状态 `server/discover` 与每请求协议元数据
- 服务端不支持新协议时，由 SDK 自动回退到旧版 `initialize` 握手
- 支持 stdio 与远程 Streamable HTTP，不维护 Aevatar 自有的第二套协议状态
- `tools/list` 分页结果按服务端最短 `ttlMs` 刷新；`cacheScope=private` 只在当前进程内复用
- 工具输出保留 `content`、任意 JSON 值的 `structuredContent` 与 `isError`

## 核心类型

- `MCPClientManager`：连接 server、发现工具、统一回收连接
- `MCPToolAdapter`：MCP tool -> `IAgentTool`
- `MCPServerConfig`：服务端配置模型
- `ServiceCollectionExtensions`：DI 注册入口

## 快速接入

```csharp
services.AddMCPTools(o => o
    .AddServer("filesystem", "npx", "-y", "@modelcontextprotocol/server-filesystem", "/tmp"));
```

远程 Streamable HTTP：

```csharp
services.AddMCPTools(o => o
    .AddRemoteServer(
        "catalog",
        "https://mcp.example.com/mcp",
        new Dictionary<string, string> { ["x-tenant"] = "demo" }));
```

`~/.aevatar/mcp.json` 同时支持 stdio 与远程端点：

```json
{
  "mcpServers": {
    "local": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"]
    },
    "catalog": {
      "url": "https://mcp.example.com/mcp",
      "headers": { "x-tenant": "demo" },
      "timeoutMs": 30000
    }
  }
}
```

`headers` 只用于部署拥有的静态路由信息。需要动态凭据的远程调用应使用
`connectors.json` 的 MCP `auth` 配置或其他正式 credential boundary。

## 依赖

- `Aevatar.AI.Abstractions`
- `ModelContextProtocol` 2.1
- `Microsoft.Extensions.*.Abstractions`
