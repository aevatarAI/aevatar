# Aevatar.Hosts.Gateway

`Aevatar.Hosts.Gateway` 是更偏“集成层”的 Web 网关，负责把配置、AI Provider、MCP、Skills 与 Runtime 组装为可运行服务。

## 职责

- 加载 `~/.aevatar` 配置
- 注册 Aevatar Runtime 与 Cognitive 模块
- 可选装配 LLM Provider（MEAI / LLMTornado）
- 可选装配 MCP Tools 与 Skills
- 提供 Chat 相关 HTTP 端点

## 关键端点

- `POST /api/chat/{agentId}`：向指定 Agent 发送消息
- `GET /api/chat/{agentId}/stream`：SSE 订阅 Agent 输出
- `POST /api/agents/workflow`：创建 Workflow Agent
- `GET /api/agents`：列出 Agent

## 主要文件

- `Program.cs`：组合式 DI 注册入口
- `ChatEndpoints.cs`：网关端点与协议转换

## 依赖

- `Aevatar.Foundation.Runtime`
- `Aevatar.Workflows.Core`
- `Aevatar.Configuration`
- `Aevatar.AI.LLMProviders.MEAI`
- `Aevatar.AI.LLMProviders.Tornado`
- `Aevatar.AI.ToolProviders.MCP`
- `Aevatar.AI.ToolProviders.Skills`
