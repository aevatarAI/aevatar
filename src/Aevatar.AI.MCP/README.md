# Aevatar.AI.MCP

`Aevatar.AI.MCP` 提供 MCP（Model Context Protocol）工具接入能力，把外部 MCP 工具桥接成 Aevatar 的 `IAgentTool`。

## 职责

- 管理 MCP Server 连接生命周期
- 发现 MCP Tool 并适配为 `IAgentTool`
- 执行工具调用并返回标准 JSON 结果
- 提供 DI 扩展 `AddMCPTools(...)`

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

## 依赖

- `Aevatar.AI`
- `ModelContextProtocol`
- `Microsoft.Extensions.*.Abstractions`
